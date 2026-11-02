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
  <div
    class="virtual-tag-table"
    :style="columnStyle"
    tabindex="0"
    role="grid"
    aria-label="Data tags"
    @keydown="onKeyDown"
  >
    <div ref="headerRef" class="virtual-tag-header" role="row" :style="headerScrollStyle">
      <span class="col-check" @pointerdown.stop>
        <input
          type="checkbox"
          :checked="allSelected"
          :disabled="total <= 0"
          :data-hint="allSelected ? 'Clear selection' : 'Select all rows'"
          :aria-label="allSelected ? 'Clear selection' : 'Select all rows'"
          @change="onHeaderCheck"
        />
      </span>
      <button
        v-if="showIdColumn"
        type="button"
        class="col-id sort-header"
        :aria-sort="ariaFor('id')"
        :data-hint="titleFor('id')"
        @pointerdown.stop
        @click="onSort('id')"
      >
        <span class="tag-col-label">ID<span v-if="markFor('id')" class="sort-mark" aria-hidden="true">{{ markFor("id") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize ID"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'id')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('id')"
        />
      </button>
      <button
        v-if="showNameColumn"
        type="button"
        class="col-name sort-header"
        :aria-sort="ariaFor('name')"
        :data-hint="titleFor('name')"
        @pointerdown.stop
        @click="onSort('name')"
      >
        <span class="tag-col-label">Name<span v-if="markFor('name')" class="sort-mark" aria-hidden="true">{{ markFor("name") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Name"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'name')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('name')"
        />
      </button>
      <button
        v-if="showDisplayNameColumn"
        type="button"
        class="col-display sort-header"
        :aria-sort="ariaFor('displayName')"
        :data-hint="titleFor('displayName')"
        @pointerdown.stop
        @click="onSort('displayName')"
      >
        <span class="tag-col-label">Display name<span v-if="markFor('displayName')" class="sort-mark" aria-hidden="true">{{ markFor("displayName") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Display name"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'display')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('display')"
        />
      </button>
      <button
        v-if="showAddressColumn"
        type="button"
        class="col-address sort-header"
        :aria-sort="ariaFor('address')"
        :data-hint="titleFor('address')"
        @pointerdown.stop
        @click="onSort('address')"
      >
        <span class="tag-col-label">Address<span v-if="markFor('address')" class="sort-mark" aria-hidden="true">{{ markFor("address") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Address"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'address')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('address')"
        />
      </button>
      <button
        v-if="showCommentColumn"
        type="button"
        class="col-comment sort-header"
        :aria-sort="ariaFor('comment')"
        :data-hint="titleFor('comment')"
        @pointerdown.stop
        @click="onSort('comment')"
      >
        <span class="tag-col-label">Comment<span v-if="markFor('comment')" class="sort-mark" aria-hidden="true">{{ markFor("comment") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Comment"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'comment')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('comment')"
        />
      </button>
      <button
        v-if="showCycleColumn"
        type="button"
        class="col-cycle sort-header"
        :aria-sort="ariaFor('cycle')"
        :data-hint="titleFor('cycle')"
        @pointerdown.stop
        @click="onSort('cycle')"
      >
        <span class="tag-col-label">Cycle<span v-if="markFor('cycle')" class="sort-mark" aria-hidden="true">{{ markFor("cycle") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Cycle"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'cycle')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('cycle')"
        />
      </button>
      <button
        v-if="showActiveColumn"
        type="button"
        class="col-active sort-header"
        :aria-sort="ariaFor('active')"
        :data-hint="titleFor('active')"
        @pointerdown.stop
        @click="onSort('active')"
      >
        <span class="tag-col-label">Active<span v-if="markFor('active')" class="sort-mark" aria-hidden="true">{{ markFor("active") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Active"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'active')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('active')"
        />
      </button>
      <button
        type="button"
        class="col-value sort-header"
        :aria-sort="ariaFor('value')"
        :data-hint="titleFor('value')"
        @pointerdown.stop
        @click="onSort('value')"
      >
        <span class="tag-col-label">Value<span v-if="markFor('value')" class="sort-mark" aria-hidden="true">{{ markFor("value") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Value"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'value')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('value')"
        />
      </button>
      <button
        type="button"
        class="col-status sort-header"
        :aria-sort="ariaFor('status')"
        :data-hint="titleFor('status')"
        @pointerdown.stop
        @click="onSort('status')"
      >
        <span class="tag-col-label">Status<span v-if="markFor('status')" class="sort-mark" aria-hidden="true">{{ markFor("status") }}</span></span>
        <span
          class="tag-col-resize"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize Status"
          data-hint="Drag to resize, double-click to fit"
          @pointerdown.stop.prevent="onResizePointerDown($event, 'status')"
          @pointermove="onResizePointerMove"
          @pointerup="onResizePointerUp"
          @pointercancel="onResizePointerUp"
          @click.stop
          @dblclick.stop="autoFitColumn('status')"
        />
      </button>
      <span class="col-chart" @pointerdown.stop>
        <button
          ref="optionsBtn"
          class="icon-btn"
          type="button"
          data-hint="Table options"
          aria-label="Table options"
          :aria-expanded="optionsOpen"
          @click="toggleOptions"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <circle cx="12" cy="6" r="1.25" />
            <circle cx="12" cy="12" r="1.25" />
            <circle cx="12" cy="18" r="1.25" />
          </svg>
        </button>
      </span>
    </div>

    <div class="virtual-tag-body-host">
      <div
        ref="parentRef"
        class="virtual-tag-body"
        @scroll="onBodyScroll"
        @pointerdown="onPointerDown"
        @dblclick="onDoubleClick"
        @contextmenu.prevent="onContextMenu"
      >
        <div v-if="total === 0" class="empty">No data tags match the filter.</div>
        <div
          v-else-if="usesScaledScroll"
          class="virtual-tag-scroll-track"
          :style="{ height: `${scrollContentHeight}px` }"
          aria-hidden="true"
        />
        <div
          v-else
          class="virtual-tag-scroll-track"
          :style="{ height: `${scrollContentHeight}px` }"
        >
          <div
            v-for="item in visibleRowSlots"
            :key="item.index"
            class="virtual-tag-row"
            :class="{ selected: isSelected(item.index), placeholder: !rowAt(item.index) }"
            :style="rowSlotStyle(item)"
            role="row"
          >
            <span class="col-check" @pointerdown.stop @click.stop>
              <input
                v-if="rowAt(item.index) && !rowAt(item.index)!.isSys"
                type="checkbox"
                :checked="selected.has(rowAt(item.index)!.id)"
                :aria-label="`Select ${rowAt(item.index)!.name}`"
                @click="onCheckboxClick(item.index, $event)"
              />
            </span>
            <span v-if="showIdColumn" class="col-id">{{ rowAt(item.index)?.id ?? "" }}</span>
            <span v-if="showNameColumn" class="col-name" :data-hint="rowAt(item.index)?.name">{{ rowAt(item.index)?.name ?? "…" }}</span>
            <span v-if="showDisplayNameColumn" class="col-display" :data-hint="rowAt(item.index)?.displayName">{{ rowAt(item.index)?.displayName ?? "" }}</span>
            <span v-if="showAddressColumn" class="col-address" :data-hint="rowAt(item.index)?.address">{{ rowAt(item.index)?.address ?? "" }}</span>
            <span v-if="showCommentColumn" class="col-comment" :data-hint="rowAt(item.index)?.comment">{{ rowAt(item.index)?.comment ?? "" }}</span>
            <span v-if="showCycleColumn" class="col-cycle">{{ rowAt(item.index)?.cycle ?? "" }}</span>
            <span v-if="showActiveColumn" class="col-active">{{ activeText(rowAt(item.index)) }}</span>
            <span class="col-value">{{ displayAt(item.index).valueText }}</span>
            <span class="col-status">
              <span
                v-if="displayAt(item.index).status"
                class="badge"
                :class="displayAt(item.index).badge"
                :data-hint="displayAt(item.index).title"
              >
                {{ displayAt(item.index).status }}
              </span>
            </span>
            <span class="col-chart" @pointerdown.stop @click.stop>
              <button
                v-if="rowAt(item.index)?.isActive"
                class="icon-btn"
                type="button"
                data-hint="Quick chart"
                :aria-label="`Quick chart for ${rowAt(item.index)!.name}`"
                @click="onChart(item.index)"
              >
                <svg viewBox="0 0 24 24" aria-hidden="true">
                  <path d="M4 19V5" />
                  <path d="M4 19h16" />
                  <path d="M7 14l3.5-4 3 2.5L20 6" />
                </svg>
              </button>
            </span>
          </div>
        </div>
      </div>
      <div
        v-if="usesScaledScroll && total > 0"
        class="virtual-tag-viewport-overlay"
        :style="overlayInsetStyle"
        @wheel="onOverlayWheel"
        @pointerdown="onPointerDown"
        @contextmenu.prevent="onContextMenu"
      >
        <div
          v-for="item in visibleRowSlots"
          :key="item.index"
          class="virtual-tag-row"
          :class="{ selected: isSelected(item.index), placeholder: !rowAt(item.index) }"
          :style="rowSlotStyle(item)"
          role="row"
        >
          <span class="col-check" @pointerdown.stop @click.stop>
            <input
              v-if="rowAt(item.index) && !rowAt(item.index)!.isSys"
              type="checkbox"
              :checked="selected.has(rowAt(item.index)!.id)"
              :aria-label="`Select ${rowAt(item.index)!.name}`"
              @click="onCheckboxClick(item.index, $event)"
            />
          </span>
          <span v-if="showIdColumn" class="col-id">{{ rowAt(item.index)?.id ?? "" }}</span>
          <span v-if="showNameColumn" class="col-name" :data-hint="rowAt(item.index)?.name">{{ rowAt(item.index)?.name ?? "…" }}</span>
          <span v-if="showDisplayNameColumn" class="col-display" :data-hint="rowAt(item.index)?.displayName">{{ rowAt(item.index)?.displayName ?? "" }}</span>
          <span v-if="showAddressColumn" class="col-address" :data-hint="rowAt(item.index)?.address">{{ rowAt(item.index)?.address ?? "" }}</span>
          <span v-if="showCommentColumn" class="col-comment" :data-hint="rowAt(item.index)?.comment">{{ rowAt(item.index)?.comment ?? "" }}</span>
          <span v-if="showCycleColumn" class="col-cycle">{{ rowAt(item.index)?.cycle ?? "" }}</span>
          <span v-if="showActiveColumn" class="col-active">{{ activeText(rowAt(item.index)) }}</span>
          <span class="col-value">{{ displayAt(item.index).valueText }}</span>
          <span class="col-status">
            <span
              v-if="displayAt(item.index).status"
              class="badge"
              :class="displayAt(item.index).badge"
              :data-hint="displayAt(item.index).title"
            >
              {{ displayAt(item.index).status }}
            </span>
          </span>
          <span class="col-chart" @pointerdown.stop @click.stop>
            <button
              v-if="rowAt(item.index)?.isActive"
              class="icon-btn"
              type="button"
              data-hint="Quick chart"
              :aria-label="`Quick chart for ${rowAt(item.index)!.name}`"
              @click="onChart(item.index)"
            >
              <svg viewBox="0 0 24 24" aria-hidden="true">
                <path d="M4 19V5" />
                <path d="M4 19h16" />
                <path d="M7 14l3.5-4 3 2.5L20 6" />
              </svg>
            </button>
          </span>
        </div>
      </div>
    </div>
    <Teleport to="body">
      <div
        v-if="optionsOpen"
        class="tag-col-menu"
        :style="menuStyle"
        @pointerdown.stop
      >
        <button type="button" class="col-menu-reset" @click="resetColumnWidths">Reset column widths</button>
        <label>
          <input type="checkbox" :checked="showIdColumn" @change="onShowIdChange" />
          Show ID
        </label>
        <label>
          <input type="checkbox" :checked="showNameColumn" @change="onShowNameChange" />
          Show Name
        </label>
        <label>
          <input type="checkbox" :checked="showDisplayNameColumn" @change="onShowDisplayNameChange" />
          Show DisplayName
        </label>
        <label>
          <input type="checkbox" :checked="showAddressColumn" @change="onShowAddressChange" />
          Show Address
        </label>
        <label>
          <input type="checkbox" :checked="showCommentColumn" @change="onShowCommentChange" />
          Show Comment
        </label>
        <label>
          <input type="checkbox" :checked="showCycleColumn" @change="onShowCycleChange" />
          Show Cycle
        </label>
        <label>
          <input type="checkbox" :checked="showActiveColumn" @change="onShowActiveChange" />
          Show Active
        </label>
      </div>
    </Teleport>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useVirtualizer } from "@tanstack/vue-virtual";
import { displayTag } from "../tagDisplay";
import { clampIndex, type SelectMode, type TagSelectEvent } from "../tagSelection";
import { ariaSort, sortMark, sortTitle, type SortKey, type SortLevel } from "../tagSort";
import type { DataTagActualValue, DataTagListItem } from "../types";

const DRAG_THRESHOLD_PX = 4;
const EDGE_PX = 48;
/** Stay below common browser max layout height (~16_777_216 px). */
const MAX_SCROLL_CONTENT_PX = 15_000_000;
const VIRTUAL_OVERSCAN = 8;
const headerRef = ref<HTMLElement | null>(null);
const optionsBtn = ref<HTMLButtonElement | null>(null);
const optionsOpen = ref(false);
const menuStyle = ref<Record<string, string>>({});
const rowHeight = ref(48);

function syncRowHeight() {
  const height = headerRef.value?.getBoundingClientRect().height ?? 0;
  if (height > 0) {
    rowHeight.value = height;
  }
}

const props = defineProps<{
  total: number;
  revision: number;
  getRow: (index: number) => DataTagListItem | undefined;
  liveById: Map<number, DataTagActualValue>;
  selected: Set<number>;
  allSelected: boolean;
  nowMs: number;
  sourceState: string;
  sort: SortLevel[];
  showIdColumn: boolean;
  showNameColumn: boolean;
  showDisplayNameColumn: boolean;
  showAddressColumn: boolean;
  showCommentColumn: boolean;
  showCycleColumn: boolean;
  showActiveColumn: boolean;
}>();

const emit = defineEmits<{
  range: [start: number, end: number, visibleIds: number[]];
  select: [event: TagSelectEvent];
  selectAll: [];
  clearSelection: [];
  sort: [key: SortKey];
  "update:showIdColumn": [value: boolean];
  "update:showNameColumn": [value: boolean];
  "update:showDisplayNameColumn": [value: boolean];
  "update:showAddressColumn": [value: boolean];
  "update:showCommentColumn": [value: boolean];
  "update:showCycleColumn": [value: boolean];
  "update:showActiveColumn": [value: boolean];
  chart: [row: DataTagListItem];
  rowContextMenu: [payload: { index: number; clientX: number; clientY: number }];
  details: [index: number];
  dismissContextMenu: [];
}>();

type TagColumnId = "id" | "name" | "display" | "address" | "comment" | "cycle" | "active" | "value" | "status";

const COLUMN_WIDTHS_KEY = "datalogxe.tagTable.columnWidths";
const COLUMN_GAP_PX = 4;
const ROW_PAD_X_PX = 24;
const CHECK_COLUMN_PX = 36;
const CHART_COLUMN_PX = 56;
const MAX_COLUMN_PX = 1600;
const COLUMN_DEFAULTS: Record<TagColumnId, { width: number; min: number }> = {
  id: { width: 72, min: 40 },
  name: { width: 300, min: 60 },
  display: { width: 200, min: 60 },
  address: { width: 320, min: 60 },
  comment: { width: 260, min: 60 },
  cycle: { width: 64, min: 44 },
  active: { width: 64, min: 44 },
  value: { width: 140, min: 50 },
  status: { width: 110, min: 60 }
};
const COLUMN_IDS = Object.keys(COLUMN_DEFAULTS) as TagColumnId[];

function defaultWidths(): Record<TagColumnId, number> {
  return Object.fromEntries(COLUMN_IDS.map((id) => [id, COLUMN_DEFAULTS[id].width])) as Record<TagColumnId, number>;
}

function clampWidth(id: TagColumnId, width: number): number {
  return Math.min(MAX_COLUMN_PX, Math.max(COLUMN_DEFAULTS[id].min, Math.round(width)));
}

function readWidths(): Record<TagColumnId, number> {
  const widths = defaultWidths();
  try {
    const raw = window.localStorage.getItem(COLUMN_WIDTHS_KEY);
    if (!raw) {
      return widths;
    }
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    for (const id of COLUMN_IDS) {
      const value = parsed[id];
      if (typeof value === "number" && Number.isFinite(value)) {
        widths[id] = clampWidth(id, value);
      }
    }
  } catch {
    // Storage can be unavailable in private mode.
  }
  return widths;
}

function writeWidths() {
  try {
    window.localStorage.setItem(COLUMN_WIDTHS_KEY, JSON.stringify(widths.value));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

const widths = ref<Record<TagColumnId, number>>(readWidths());

const visibleColumnIds = computed(() => {
  const ids: TagColumnId[] = [];
  if (props.showIdColumn) ids.push("id");
  if (props.showNameColumn) ids.push("name");
  if (props.showDisplayNameColumn) ids.push("display");
  if (props.showAddressColumn) ids.push("address");
  if (props.showCommentColumn) ids.push("comment");
  if (props.showCycleColumn) ids.push("cycle");
  if (props.showActiveColumn) ids.push("active");
  ids.push("value", "status");
  return ids;
});

const minTableWidth = computed(() => {
  const ids = visibleColumnIds.value;
  const sum = ids.reduce((total, id) => total + widths.value[id], CHECK_COLUMN_PX + CHART_COLUMN_PX);
  return sum + COLUMN_GAP_PX * (ids.length + 1) + ROW_PAD_X_PX;
});

const columnStyle = computed(() => {
  const ids = visibleColumnIds.value;
  // The last column takes the rest of the width, so the chart button stays at the right edge.
  const tracks = [`${CHECK_COLUMN_PX}px`, ...ids.map((id, i) =>
    i === ids.length - 1 ? `minmax(${widths.value[id]}px, 1fr)` : `${widths.value[id]}px`), `${CHART_COLUMN_PX}px`];
  return { "--tag-columns": tracks.join(" "), "--tag-min-width": `${minTableWidth.value}px` };
});

const scrollLeft = ref(0);

// The header keeps the gutter of the vertical scrollbar, so it is that much wider than a row.
const headerScrollStyle = computed(() => ({
  transform: `translateX(${-scrollLeft.value}px)`,
  minWidth: `${minTableWidth.value + scrollbarWidth.value}px`
}));

let resizeDrag: { id: TagColumnId; startX: number; startWidth: number } | null = null;

function onResizePointerDown(event: PointerEvent, id: TagColumnId) {
  if (event.button !== 0) {
    return;
  }
  try {
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  } catch {
    // The pointer can already be gone. Moves still update the width.
  }
  resizeDrag = { id, startX: event.clientX, startWidth: widths.value[id] };
  document.body.style.cursor = "col-resize";
  document.body.style.userSelect = "none";
}

function onResizePointerMove(event: PointerEvent) {
  if (!resizeDrag) {
    return;
  }
  const next = clampWidth(resizeDrag.id, resizeDrag.startWidth + event.clientX - resizeDrag.startX);
  if (widths.value[resizeDrag.id] !== next) {
    widths.value = { ...widths.value, [resizeDrag.id]: next };
  }
}

function onResizePointerUp() {
  if (!resizeDrag) {
    return;
  }
  resizeDrag = null;
  document.body.style.cursor = "";
  document.body.style.userSelect = "";
  writeWidths();
}

function contentWidth(element: Element): number {
  const range = document.createRange();
  range.selectNodeContents(element);
  return range.getBoundingClientRect().width;
}

/** Fits a column to its header and to the rows in the viewport. */
function autoFitColumn(id: TagColumnId) {
  onResizePointerUp();
  let widest = 0;
  const label = headerRef.value?.querySelector(`.col-${id} .tag-col-label`);
  if (label) {
    widest = contentWidth(label) + 10;
  }
  const host = parentRef.value?.parentElement;
  const body = parentRef.value;
  if (host && body) {
    const view = body.getBoundingClientRect();
    for (const cell of host.querySelectorAll<HTMLElement>(`.virtual-tag-row:not(.placeholder) .col-${id}`)) {
      const rect = cell.getBoundingClientRect();
      if (rect.bottom <= view.top || rect.top >= view.bottom) {
        continue;
      }
      widest = Math.max(widest, contentWidth(cell));
    }
  }
  if (widest <= 0) {
    return;
  }
  widths.value = { ...widths.value, [id]: clampWidth(id, Math.ceil(widest) + 4) };
  writeWidths();
}

function resetColumnWidths() {
  widths.value = defaultWidths();
  writeWidths();
  optionsOpen.value = false;
}

const parentRef = ref<HTMLElement | null>(null);
const scrollbarWidth = ref(0);

const scrollTick = ref(0);

const overlayInsetStyle = computed(() => ({
  right: `${scrollbarWidth.value > 0 ? scrollbarWidth.value : 17}px`
}));

function syncScrollbarWidth() {
  const body = parentRef.value;
  if (!body) {
    return;
  }
  scrollbarWidth.value = Math.max(0, body.offsetWidth - body.clientWidth);
}

const linearContentHeight = computed(() => props.total * rowHeight.value);

const usesScaledScroll = computed(
  () => props.total > 0 && linearContentHeight.value > MAX_SCROLL_CONTENT_PX
);

const scrollContentHeight = computed(() => {
  if (props.total <= 0) {
    return 0;
  }
  if (usesScaledScroll.value) {
    return MAX_SCROLL_CONTENT_PX;
  }
  return linearContentHeight.value;
});

function scaledScrollMetrics(body: HTMLElement): { maxScroll: number; maxIndex: number } {
  const maxScroll = Math.max(0, body.scrollHeight - body.clientHeight);
  const viewportRows = rowHeight.value > 0 ? body.clientHeight / rowHeight.value : 0;
  const maxIndex = Math.max(0, props.total - viewportRows);
  return { maxScroll, maxIndex };
}

function exactIndexFromScrollTop(scrollTop: number): number {
  if (props.total <= 1) {
    return 0;
  }
  const body = parentRef.value;
  if (!body) {
    return 0;
  }
  const { maxScroll, maxIndex } = scaledScrollMetrics(body);
  if (maxScroll <= 0 || maxIndex <= 0) {
    return 0;
  }
  const clamped = Math.max(0, Math.min(scrollTop, maxScroll));
  return (clamped / maxScroll) * maxIndex;
}

const virtualizer = useVirtualizer(
  computed(() => ({
    count: props.total,
    getScrollElement: () => parentRef.value,
    estimateSize: () => rowHeight.value,
    overscan: VIRTUAL_OVERSCAN,
    enabled: !usesScaledScroll.value
  }))
);

function scrollOffsetToIndex(offset: number): number {
  if (props.total <= 0) {
    return 0;
  }
  if (props.total === 1) {
    return 0;
  }
  if (!usesScaledScroll.value) {
    return clampIndex(Math.floor(offset / rowHeight.value), props.total);
  }
  return clampIndex(Math.round(exactIndexFromScrollTop(offset)), props.total);
}

const visibleRowSlots = computed(() => {
  void scrollTick.value;
  if (props.total <= 0) {
    return [];
  }
  if (!usesScaledScroll.value) {
    return virtualizer.value.getVirtualItems().map((item) => ({
      index: item.index,
      start: item.start
    }));
  }
  const body = parentRef.value;
  if (!body) {
    return [];
  }
  // Layout rows at full rowHeight in the sticky viewport. Do not use compressed
  // contentY offsets — that stacks ~480k rows into 15M px and makes them look thin.
  const exact = exactIndexFromScrollTop(body.scrollTop);
  const viewportRows = Math.ceil(body.clientHeight / rowHeight.value);
  const from = Math.max(0, Math.floor(exact) - VIRTUAL_OVERSCAN);
  const to = Math.min(props.total - 1, Math.ceil(exact + viewportRows) + VIRTUAL_OVERSCAN);
  const slots: { index: number; start?: number; localY?: number }[] = [];
  for (let i = from; i <= to; i++) {
    slots.push({ index: i, localY: (i - exact) * rowHeight.value });
  }
  return slots;
});

function rowSlotStyle(item: { start?: number; localY?: number }): Record<string, string> {
  if (usesScaledScroll.value) {
    return { transform: `translate(${-scrollLeft.value}px, ${item.localY ?? 0}px)` };
  }
  return { transform: `translateY(${item.start ?? 0}px)` };
}

let gesture: {
  startX: number;
  startY: number;
  lastY: number;
  mode: SelectMode;
  dragging: boolean;
} | null = null;
let autoScrollRaf = 0;

function rowAt(index: number): DataTagListItem | undefined {
  return props.getRow(index);
}

function isSelected(index: number): boolean {
  const row = props.getRow(index);
  return !!row && props.selected.has(row.id);
}

function displayAt(index: number) {
  const row = props.getRow(index);
  return displayTag(row, row ? props.liveById.get(row.id) : undefined, props.nowMs, props.sourceState);
}

function activeText(row: DataTagListItem | undefined): string {
  if (!row) {
    return "";
  }
  return row.isActive ? "Yes" : "No";
}

function onBodyScroll() {
  scrollTick.value += 1;
  scrollLeft.value = parentRef.value?.scrollLeft ?? 0;
  syncScrollbarWidth();
  optionsOpen.value = false;
  emit("dismissContextMenu");
  emitRange();
}

function onOverlayWheel(event: WheelEvent) {
  const body = parentRef.value;
  if (!body) {
    return;
  }
  event.preventDefault();
  body.scrollTop += event.deltaY;
}

function toggleOptions() {
  optionsOpen.value = !optionsOpen.value;
  if (optionsOpen.value) {
    placeOptionsMenu();
  }
}

function placeOptionsMenu() {
  const button = optionsBtn.value;
  if (!button) {
    return;
  }
  const rect = button.getBoundingClientRect();
  menuStyle.value = {
    top: `${Math.round(rect.bottom + 4)}px`,
    left: `${Math.round(rect.right)}px`
  };
}

function onShowIdChange(event: Event) {
  const input = event.target as HTMLInputElement;
  emit("update:showIdColumn", input.checked);
}

function onShowNameChange(event: Event) {
  const input = event.target as HTMLInputElement;
  emit("update:showNameColumn", input.checked);
}

function onShowDisplayNameChange(event: Event) {
  const input = event.target as HTMLInputElement;
  emit("update:showDisplayNameColumn", input.checked);
}

function onShowAddressChange(event: Event) {
  const input = event.target as HTMLInputElement;
  emit("update:showAddressColumn", input.checked);
}

function onShowCommentChange(event: Event) {
  const input = event.target as HTMLInputElement;
  emit("update:showCommentColumn", input.checked);
}

function onShowCycleChange(event: Event) {
  const input = event.target as HTMLInputElement;
  emit("update:showCycleColumn", input.checked);
}

function onShowActiveChange(event: Event) {
  const input = event.target as HTMLInputElement;
  emit("update:showActiveColumn", input.checked);
}

function onOptionsPointerDown(event: PointerEvent) {
  if (!optionsOpen.value) {
    return;
  }
  const target = event.target as Node | null;
  if (target && (optionsBtn.value?.contains(target) || (target instanceof Element && target.closest(".tag-col-menu")))) {
    return;
  }
  optionsOpen.value = false;
}

function emitRange() {
  const items = visibleRowSlots.value;
  if (items.length === 0) {
    emit("range", 0, -1, []);
    return;
  }
  const ids: number[] = [];
  for (const item of items) {
    const row = props.getRow(item.index);
    if (row) {
      ids.push(row.id);
    }
  }
  emit("range", items[0].index, items[items.length - 1].index, ids);
}

function selectModeFromEvent(event: PointerEvent | MouseEvent): SelectMode {
  if (event.shiftKey) {
    return "range";
  }
  if (event.ctrlKey || event.metaKey) {
    return "toggle";
  }
  return "replace";
}

function indexAtClientY(clientY: number): number {
  const body = parentRef.value;
  if (!body || props.total <= 0) {
    return 0;
  }
  const top = body.getBoundingClientRect().top;
  const yInViewport = clientY - top;
  if (!usesScaledScroll.value) {
    return scrollOffsetToIndex(body.scrollTop + yInViewport);
  }
  const exact = exactIndexFromScrollTop(body.scrollTop);
  return clampIndex(Math.floor(exact + yInViewport / rowHeight.value), props.total);
}

function emitSelect(index: number, mode: SelectMode, dragging: boolean) {
  emit("select", { index, mode, dragging });
}

function onCheckboxClick(index: number, event: MouseEvent) {
  const row = props.getRow(index);
  if (!row || row.isSys) {
    event.preventDefault();
    return;
  }
  emitSelect(index, selectModeFromEvent(event) === "range" ? "range" : "toggle", false);
  // The click is not cancelled: a cancelled one makes the browser restore the old state of the box after the update
  // has set the new one, so the box never showed the selection. The box toggled itself, which a click with Shift
  // does not always follow (a row that is in the range stays selected), so it is set to the selection
  const input = event.target as HTMLInputElement;
  void nextTick(() => {
    input.checked = props.selected.has(row.id);
  });
}

function onPointerDown(event: PointerEvent) {
  if (event.button !== 0 || props.total <= 0) {
    return;
  }
  const target = event.target as HTMLElement | null;
  if (
    !target?.closest(".virtual-tag-row") &&
    !target?.closest(".virtual-tag-spacer") &&
    !target?.closest(".virtual-tag-scroll-track") &&
    !target?.closest(".virtual-tag-viewport-overlay")
  ) {
    return;
  }
  ((event.currentTarget as HTMLElement | null)?.closest(".virtual-tag-table") as HTMLElement | null)
    ?.focus({ preventScroll: true });
  const index = indexAtClientY(event.clientY);
  const row = props.getRow(index);
  if (row?.isSys) {
    return;
  }

  const mode = selectModeFromEvent(event);
  gesture = {
    startX: event.clientX,
    startY: event.clientY,
    lastY: event.clientY,
    mode,
    dragging: false
  };
  emitSelect(index, mode, false);
  window.addEventListener("pointermove", onPointerMove, { passive: false });
  window.addEventListener("pointerup", onPointerUp);
  window.addEventListener("pointercancel", onPointerUp);
}

function onKeyDown(event: KeyboardEvent) {
  if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== "a") {
    return;
  }
  if (props.total <= 0) {
    return;
  }
  event.preventDefault();
  emit("selectAll");
}

function scrollToIndex(index: number) {
  const body = parentRef.value;
  if (!body || props.total <= 0) {
    return;
  }
  const row = clampIndex(index, props.total);
  if (!usesScaledScroll.value) {
    virtualizer.value.scrollToIndex(row, { align: "auto" });
    return;
  }
  const { maxScroll, maxIndex } = scaledScrollMetrics(body);
  const exact = exactIndexFromScrollTop(body.scrollTop);
  const viewportRows = rowHeight.value > 0 ? body.clientHeight / rowHeight.value : 0;
  if (row >= exact && row < exact + viewportRows) {
    return;
  }
  let topIndex = row;
  if (row > exact) {
    topIndex = Math.max(0, row + 1 - viewportRows);
  }
  const next = maxIndex <= 0 ? 0 : (Math.min(topIndex, maxIndex) / maxIndex) * maxScroll;
  body.scrollTop = Math.max(0, Math.min(maxScroll, next));
  scrollTick.value += 1;
  emitRange();
}

/** Puts the row at the top of the viewport (restoring a position after a reload). */
function scrollToTop(index: number) {
  const body = parentRef.value;
  if (!body || props.total <= 0) {
    return;
  }
  const row = clampIndex(index, props.total);
  if (!usesScaledScroll.value) {
    virtualizer.value.scrollToIndex(row, { align: "start" });
  } else {
    const { maxScroll, maxIndex } = scaledScrollMetrics(body);
    body.scrollTop = maxIndex <= 0 ? 0 : (Math.min(row, maxIndex) / maxIndex) * maxScroll;
  }
  scrollTick.value += 1;
  emitRange();
}

/** The first row in the viewport (not counting the overscan rows above it). */
function topIndex(): number {
  const body = parentRef.value;
  if (!body || props.total <= 0) {
    return 0;
  }
  if (!usesScaledScroll.value) {
    return clampIndex(Math.round(body.scrollTop / rowHeight.value), props.total);
  }
  return clampIndex(Math.round(exactIndexFromScrollTop(body.scrollTop)), props.total);
}

defineExpose({ scrollToIndex, scrollToTop, topIndex });

function onPointerMove(event: PointerEvent) {
  if (!gesture) {
    return;
  }
  gesture.lastY = event.clientY;
  if (!gesture.dragging) {
    const dx = event.clientX - gesture.startX;
    const dy = event.clientY - gesture.startY;
    if (dx * dx + dy * dy < DRAG_THRESHOLD_PX * DRAG_THRESHOLD_PX) {
      return;
    }
    gesture.dragging = true;
    gesture.mode = "range";
    startAutoScroll();
  }
  event.preventDefault();
  emitSelect(indexAtClientY(event.clientY), "range", true);
}

function onPointerUp() {
  if (!gesture) {
    return;
  }
  if (gesture.dragging) {
    emitSelect(indexAtClientY(gesture.lastY), "range", false);
  }
  endGesture();
}

function startAutoScroll() {
  if (autoScrollRaf) {
    return;
  }
  const tick = () => {
    autoScrollRaf = 0;
    if (!gesture?.dragging) {
      return;
    }
    const body = parentRef.value;
    if (body) {
      const rect = body.getBoundingClientRect();
      let dy = 0;
      if (gesture.lastY < rect.top + EDGE_PX) {
        dy = -rowHeight.value * 1.5;
      } else if (gesture.lastY > rect.bottom - EDGE_PX) {
        dy = rowHeight.value * 1.5;
      }
      if (dy !== 0) {
        body.scrollTop += dy;
        emitSelect(indexAtClientY(gesture.lastY), "range", true);
      }
    }
    autoScrollRaf = window.requestAnimationFrame(tick);
  };
  autoScrollRaf = window.requestAnimationFrame(tick);
}

function endGesture() {
  gesture = null;
  if (autoScrollRaf) {
    window.cancelAnimationFrame(autoScrollRaf);
    autoScrollRaf = 0;
  }
  window.removeEventListener("pointermove", onPointerMove);
  window.removeEventListener("pointerup", onPointerUp);
  window.removeEventListener("pointercancel", onPointerUp);
}

function onHeaderCheck(event: Event) {
  const input = event.target as HTMLInputElement;
  if (input.checked) {
    emit("selectAll");
  } else {
    emit("clearSelection");
  }
}

function onSort(key: SortKey) {
  emit("sort", key);
}

function onChart(index: number) {
  const row = props.getRow(index);
  if (row?.isActive) {
    emit("chart", row);
  }
}

// A double click on a row opens its details; the check box and the chart button keep their own clicks
function onDoubleClick(event: MouseEvent) {
  if (event.button !== 0 || props.total <= 0) {
    return;
  }
  const target = event.target as HTMLElement | null;
  if (!target?.closest(".virtual-tag-row") || target.closest(".col-check, .col-chart")) {
    return;
  }
  const index = indexAtClientY(event.clientY);
  if (props.getRow(index)) {
    emit("details", index);
  }
}

function onContextMenu(event: MouseEvent) {
  if (props.total <= 0) {
    return;
  }
  const target = event.target as HTMLElement | null;
  if (!target?.closest(".virtual-tag-row")) {
    return;
  }
  const index = indexAtClientY(event.clientY);
  const row = props.getRow(index);
  if (!row || row.isSys) {
    return;
  }
  emit("rowContextMenu", {
    index,
    clientX: event.clientX,
    clientY: event.clientY
  });
}

function ariaFor(key: SortKey) {
  return ariaSort(props.sort, key);
}

function titleFor(key: SortKey) {
  return sortTitle(props.sort, key);
}

function markFor(key: SortKey) {
  return sortMark(props.sort, key);
}

watch(visibleRowSlots, emitRange, { immediate: true });
watch(() => props.revision, () => {
  scrollTick.value += 1;
  emitRange();
});

watch(rowHeight, () => {
  if (!usesScaledScroll.value) {
    virtualizer.value.measure();
  }
  scrollTick.value += 1;
});

watch(
  () => props.total,
  () => {
    const body = parentRef.value;
    if (!body) {
      return;
    }
    const maxScrollTop = Math.max(0, scrollContentHeight.value - body.clientHeight);
    if (body.scrollTop > maxScrollTop) {
      body.scrollTop = maxScrollTop;
    }
    scrollTick.value += 1;
  }
);

watch(usesScaledScroll, () => {
  requestAnimationFrame(() => syncScrollbarWidth());
});

onMounted(() => {
  syncRowHeight();
  syncScrollbarWidth();
  window.addEventListener("pointerdown", onOptionsPointerDown);
  window.addEventListener("resize", syncScrollbarWidth);
});

onUnmounted(() => {
  endGesture();
  window.removeEventListener("pointerdown", onOptionsPointerDown);
  window.removeEventListener("resize", syncScrollbarWidth);
});
</script>
