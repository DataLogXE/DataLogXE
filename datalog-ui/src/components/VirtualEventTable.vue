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
    class="virtual-event-table"
    :style="columnStyle"
    tabindex="0"
    role="grid"
    aria-label="Alarms"
  >
    <div ref="headerScrollRef" class="virtual-event-header-scroll">
      <div
        ref="headerRef"
        class="virtual-event-header"
        role="row"
        :class="{ 'is-col-dragging': draggingId !== null }"
      >
        <div
          v-for="column in visibleColumns"
          :key="column.id"
          class="event-col-head"
          :class="headClass(column.id)"
          role="columnheader"
          :data-column-id="column.id"
          :aria-sort="sortable ? ariaSort(column.id) : undefined"
          @pointerdown="onHeaderPointerDown($event, column.id)"
        >
          <span
            class="event-col-label"
            :class="{ 'is-sortable': sortable }"
            :data-hint="sortTitle(column.id, column.label)"
          >
            <span class="event-col-text">{{ column.label }}</span>
            <span v-if="sortMark(column.id)" class="event-sort-mark" aria-hidden="true">{{ sortMark(column.id) }}</span>
          </span>
          <span
            class="event-col-resize"
            role="separator"
            aria-orientation="vertical"
            :aria-label="`Resize ${column.label}`"
            data-hint="Drag to resize, double-click to fit"
            @pointerdown="onResizePointerDown($event, column.id)"
            @dblclick.stop="autoFitColumn(column.id)"
            @pointermove="onResizePointerMove"
            @pointerup="onResizePointerUp"
            @pointercancel="onResizePointerUp"
          />
        </div>
      </div>
    </div>
    <div ref="parentRef" class="virtual-event-body" @scroll="onScroll">
      <div v-if="rowCount === 0" class="empty">{{ emptyText }}</div>
      <template v-else>
        <div class="virtual-event-spacer" :style="{ height: `${spacerHeight}px` }">
          <template v-if="!usesScaledScroll">
            <div
              v-for="slot in visibleSlots"
              :key="rowKey(slot.index)"
              class="virtual-event-row"
              :class="rowClass(slot.index)"
              :data-hint="rowOfflineTitle(rowAt(slot.index))"
              :style="slotStyle(slot, rowAt(slot.index))"
              role="row"
              @click="onRowClick(slot.index)"
              @contextmenu="onRowContextMenu($event, slot.index)"
            >
              <span
                v-for="column in visibleColumns"
                :key="column.id"
                :class="cellClass(column.id)"
                :data-hint="cellTitle(column.id, rowAt(slot.index))"
              >
                <template v-if="column.id === 'state' && rowAt(slot.index)">
                  <AlarmStateIcon
                    :state="rowAt(slot.index)?.alarmState"
                    :limit="limitOf(rowAt(slot.index))"
                  />
                  <span>{{ stateLabel(rowAt(slot.index)) }}</span>
                </template>
                <template v-else>{{ cellText(column.id, rowAt(slot.index)) }}</template>
              </span>
            </div>
          </template>
        </div>
        <div
          v-if="usesScaledScroll"
          class="virtual-event-viewport-overlay"
          :style="overlayInsetStyle"
        >
          <div
            v-for="slot in visibleSlots"
            :key="rowKey(slot.index)"
            class="virtual-event-row"
            :class="rowClass(slot.index)"
            :data-hint="rowOfflineTitle(rowAt(slot.index))"
            :style="slotStyle(slot, rowAt(slot.index))"
            role="row"
            @click="onRowClick(slot.index)"
            @contextmenu="onRowContextMenu($event, slot.index)"
          >
            <span
              v-for="column in visibleColumns"
              :key="column.id"
              :class="cellClass(column.id)"
              :data-hint="cellTitle(column.id, rowAt(slot.index))"
            >
              <template v-if="column.id === 'state' && rowAt(slot.index)">
                <AlarmStateIcon
                  :state="rowAt(slot.index)?.alarmState"
                  :limit="limitOf(rowAt(slot.index))"
                />
                <span>{{ stateLabel(rowAt(slot.index)) }}</span>
              </template>
              <template v-else>{{ cellText(column.id, rowAt(slot.index)) }}</template>
            </span>
          </div>
        </div>
      </template>
    </div>
    <div class="col-options" @pointerdown.stop>
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
    </div>
    <Teleport to="body">
      <div v-if="rowMenu" class="tag-context-menu-backdrop" @click="closeRowMenu" @contextmenu.prevent="closeRowMenu">
        <div
          ref="rowMenuRef"
          class="chart-plot-menu"
          role="menu"
          tabindex="-1"
          :style="{ left: `${rowMenu.left}px`, top: `${rowMenu.top}px` }"
          @click.stop
          @contextmenu.prevent.stop
          @mouseleave="closeRowMenu"
          @keydown.esc.prevent.stop="closeRowMenu"
        >
          <button
            v-if="isVisible('source')"
            class="chart-plot-menu-item"
            type="button"
            role="menuitem"
            :disabled="!rowMenu.sourceId"
            @click="filterBySource"
          >
            <span>Filter Source</span>
            <span class="chart-plot-menu-hint">{{ rowMenu.sourceName }}</span>
          </button>
          <button
            v-if="isVisible('class')"
            class="chart-plot-menu-item"
            type="button"
            role="menuitem"
            :disabled="!rowMenu.classLabel"
            @click="filterByClass"
          >
            <span>Filter Class</span>
            <span class="chart-plot-menu-hint">{{ rowMenu.classLabel }}</span>
          </button>
          <button
            v-if="isVisible('state') && stateFilterOf"
            class="chart-plot-menu-item"
            type="button"
            role="menuitem"
            :disabled="!rowMenu.state"
            @click="filterByState"
          >
            <span>Filter State</span>
            <span class="chart-plot-menu-hint">{{ rowMenu.stateLabel }}</span>
          </button>
        </div>
      </div>
    </Teleport>
    <Teleport to="body">
      <div
        v-if="optionsOpen"
        class="tag-col-menu"
        :style="menuStyle"
        @pointerdown.stop
      >
        <button type="button" class="col-menu-reset" @click="resetColumns">Reset</button>
        <template v-for="column in menuColumns" :key="column.id">
          <label>
            <input
              type="checkbox"
              :checked="isVisible(column.id)"
              :disabled="isOnlyVisible(column.id)"
              @change="onShowChange(column.id, $event)"
            />
            {{ column.menuLabel }}
          </label>
          <label v-if="column.id === 'time'" class="col-menu-sub">
            <input
              type="checkbox"
              :checked="showTimeMilliseconds"
              @change="onMillisecondsChange"
            />
            Milliseconds
          </label>
        </template>
      </div>
    </Teleport>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useVirtualizer } from "@tanstack/vue-virtual";
import { alarmClassLabel, alarmStateBase, formatAlarmState, formatClassName, formatDateTime, formatDateTimeMs, formatInteger, formatDurationMs, formatYesNo, limitStateFromProps } from "../format";
import AlarmStateIcon from "./AlarmStateIcon.vue";
import type { EventLogItem } from "../types";

// The key of the time before every page had its own columns: read once while a page has none of its own yet
const LEGACY_COLUMN_PREFS_KEY = "alarmColumns";
const COLUMN_GAP_PX = 8;
const COLUMN_PAD_X_PX = 22;
const DRAG_THRESHOLD_PX = 4;
const MAX_COLUMN_PX = 1200;
/** Stay below common browser max layout height (~16_777_216 px). */
const MAX_SCROLL_CONTENT_PX = 15_000_000;
const VIRTUAL_OVERSCAN = 12;

type AlarmColumnId =
  | "time"
  | "state"
  | "severity"
  | "active"
  | "acked"
  | "retain"
  | "duration"
  | "source"
  | "class"
  | "type"
  | "message"
  | "exception";

type AlarmColumnDef = {
  id: AlarmColumnId;
  label: string;
  menuLabel: string;
  defaultWidth: number;
  minWidth: number;
};

type AlarmColumnState = {
  id: AlarmColumnId;
  width: number;
  visible: boolean;
};

type AlarmColumnPrefs = {
  order?: unknown;
  widths?: Record<string, unknown>;
  visible?: Record<string, unknown>;
  showTimeMilliseconds?: unknown;
};

const ALARM_COLUMNS: AlarmColumnDef[] = [
  { id: "time", label: "Time", menuLabel: "Show Time", defaultWidth: 256, minWidth: 120 },
  { id: "state", label: "State", menuLabel: "Show State", defaultWidth: 220, minWidth: 96 },
  { id: "severity", label: "Severity", menuLabel: "Show Severity", defaultWidth: 72, minWidth: 64 },
  { id: "active", label: "Active", menuLabel: "Show Active", defaultWidth: 64, minWidth: 56 },
  { id: "acked", label: "Acked", menuLabel: "Show Acked", defaultWidth: 64, minWidth: 56 },
  { id: "retain", label: "Retain", menuLabel: "Show Retain", defaultWidth: 64, minWidth: 56 },
  { id: "duration", label: "Duration", menuLabel: "Show Duration", defaultWidth: 104, minWidth: 80 },
  { id: "source", label: "Source", menuLabel: "Show Source", defaultWidth: 160, minWidth: 80 },
  { id: "class", label: "Class", menuLabel: "Show Class", defaultWidth: 120, minWidth: 64 },
  { id: "type", label: "Type", menuLabel: "Show Type", defaultWidth: 168, minWidth: 80 },
  { id: "message", label: "Message", menuLabel: "Show Message", defaultWidth: 320, minWidth: 96 },
  { id: "exception", label: "Exception", menuLabel: "Show Exception", defaultWidth: 240, minWidth: 96 }
];

const DEFAULT_COLUMN_ORDER: AlarmColumnId[] = [
  "time",
  "source",
  "class",
  "message",
  "duration",
  "state",
  "severity",
  "active",
  "acked",
  "retain",
  "type",
  "exception"
];

const DEFAULT_VISIBLE_COLUMNS = new Set<AlarmColumnId>(["time", "source", "class", "message", "duration"]);

function defById(id: AlarmColumnId): AlarmColumnDef {
  const found = ALARM_COLUMNS.find((column) => column.id === id);
  if (!found) {
    return ALARM_COLUMNS[0];
  }
  return found;
}

function isAlarmColumnId(value: unknown): value is AlarmColumnId {
  return typeof value === "string" && ALARM_COLUMNS.some((column) => column.id === value);
}

function defaultColumns(): AlarmColumnState[] {
  return DEFAULT_COLUMN_ORDER.map((id) => ({
    id,
    width: defById(id).defaultWidth,
    visible: DEFAULT_VISIBLE_COLUMNS.has(id)
  }));
}

function readStoredPrefs(): string | null {
  return window.localStorage.getItem(props.storageKey) ?? window.localStorage.getItem(LEGACY_COLUMN_PREFS_KEY);
}

function readColumns(): AlarmColumnState[] {
  try {
    const raw = readStoredPrefs();
    if (!raw) {
      return defaultColumns();
    }
    const parsed = JSON.parse(raw) as AlarmColumnPrefs;
    const order = Array.isArray(parsed.order) ? parsed.order.filter(isAlarmColumnId) : [];
    for (const id of DEFAULT_COLUMN_ORDER) {
      if (order.includes(id)) {
        continue;
      }
      let insertAt = order.length;
      const preferred = DEFAULT_COLUMN_ORDER.indexOf(id);
      for (let later = preferred + 1; later < DEFAULT_COLUMN_ORDER.length; later++) {
        const found = order.indexOf(DEFAULT_COLUMN_ORDER[later]);
        if (found >= 0) {
          insertAt = found;
          break;
        }
      }
      order.splice(insertAt, 0, id);
    }
    const columns = order.map((id) => {
      const def = defById(id);
      const widthRaw = parsed.widths?.[id];
      const width = typeof widthRaw === "number" && Number.isFinite(widthRaw)
        ? Math.min(MAX_COLUMN_PX, Math.max(def.minWidth, Math.round(widthRaw)))
        : def.defaultWidth;
      const savedVisible = parsed.visible?.[id];
      return {
        id,
        width,
        visible: typeof savedVisible === "boolean" ? savedVisible : DEFAULT_VISIBLE_COLUMNS.has(id)
      };
    });
    if (!columns.some((column) => column.visible)) {
      columns[0].visible = true;
    }
    return columns;
  } catch {
    return defaultColumns();
  }
}

function readShowTimeMilliseconds(): boolean {
  try {
    const raw = readStoredPrefs();
    if (!raw) {
      return false;
    }
    const parsed = JSON.parse(raw) as AlarmColumnPrefs;
    return parsed.showTimeMilliseconds === true;
  } catch {
    return false;
  }
}

const props = withDefaults(
  defineProps<{
    /** The key in the browser that keeps the columns of this table; every page has its own. */
    storageKey: string;
    items: EventLogItem[];
    selectedIndex: number;
    sourceNameById?: Record<number, string>;
    offlineSourceIds?: ReadonlySet<number>;
    updatesStopped?: boolean;
    emptyText?: string;
    total?: number | null;
    getRow?: (index: number) => EventLogItem | undefined;
    revision?: number;
    sortable?: boolean;
    sortColumn?: string | null;
    sortDirection?: "asc" | "desc";
    /** The id of the state filter that shows the state of a row (empty when it has none); a page without a state filter leaves it out. */
    stateFilterOf?: (row: EventLogItem) => string;
  }>(),
  {
    sourceNameById: () => ({}),
    offlineSourceIds: undefined,
    updatesStopped: false,
    emptyText: "No alarms in this time range.",
    total: null,
    revision: 0,
    sortable: false,
    sortColumn: null,
    sortDirection: "asc"
  }
);

const columns = ref<AlarmColumnState[]>(readColumns());
const showTimeMilliseconds = ref(readShowTimeMilliseconds());
const menuColumns = computed(() => DEFAULT_COLUMN_ORDER.map((id) => defById(id)));
const headerRef = ref<HTMLElement | null>(null);
// The box of the header that scrolls with the body, which is the one that scrolls sideways
const headerScrollRef = ref<HTMLElement | null>(null);
const optionsBtn = ref<HTMLButtonElement | null>(null);
const optionsOpen = ref(false);
const menuStyle = ref<Record<string, string>>({});
const rowHeight = ref(36);
const draggingId = ref<AlarmColumnId | null>(null);
const dragOverId = ref<AlarmColumnId | null>(null);
const dragOverAfter = ref(false);

type MoveDrag = {
  id: AlarmColumnId;
  startX: number;
  startY: number;
  pointerId: number;
  active: boolean;
};

type ResizeDrag = {
  id: AlarmColumnId;
  startX: number;
  startWidth: number;
};

let moveDrag: MoveDrag | null = null;
let resizeDrag: ResizeDrag | null = null;

const visibleColumns = computed(() =>
  columns.value
    .filter((column) => column.visible)
    .map((column) => ({ ...defById(column.id), width: column.width }))
);

const columnStyle = computed(() => {
  const visible = visibleColumns.value;
  const tracks = visible.map((column) => `${column.width}px`);
  const widthSum = visible.reduce((sum, column) => sum + column.width, 0);
  const gaps = COLUMN_GAP_PX * Math.max(0, visible.length - 1);
  const minWidth = widthSum + gaps + COLUMN_PAD_X_PX;
  return {
    "--event-columns": tracks.join(" "),
    "--event-min-width": `${minWidth}px`,
    // The vertical scrollbar of the body: the header scrolls as far sideways as the body does
    "--event-gutter": `${scrollbarWidth.value}px`
  };
});

function writeColumns() {
  try {
    const prefs = {
      order: columns.value.map((column) => column.id),
      widths: Object.fromEntries(columns.value.map((column) => [column.id, column.width])),
      visible: Object.fromEntries(columns.value.map((column) => [column.id, column.visible])),
      showTimeMilliseconds: showTimeMilliseconds.value
    };
    window.localStorage.setItem(props.storageKey, JSON.stringify(prefs));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

function syncRowHeight() {
  const height = headerRef.value?.getBoundingClientRect().height ?? 0;
  if (height > 0) {
    rowHeight.value = height;
  }
}

const emit = defineEmits<{
  select: [index: number];
  sort: [columnId: AlarmColumnId];
  reset: [];
  range: [start: number, end: number];
  "filter-source": [sourceId: number];
  "filter-class": [label: string];
  "filter-state": [state: string];
}>();

const parentRef = ref<HTMLElement | null>(null);
const scrollTick = ref(0);
const scrollbarWidth = ref(0);

const rowCount = computed(() => (props.total == null ? props.items.length : props.total));

const linearContentHeight = computed(() => rowCount.value * rowHeight.value);

const usesScaledScroll = computed(
  () => props.total != null && rowCount.value > 0 && linearContentHeight.value > MAX_SCROLL_CONTENT_PX
);

// The overlay lives inside the scrolling body, so it has to follow the scroll position. Otherwise
// it scrolls out of view with the spacer and a jump deep into a large range shows an empty table.
const overlayInsetStyle = computed(() => {
  void scrollTick.value;
  const body = parentRef.value;
  return {
    width: `max(calc(100% - ${scrollbarWidth.value}px), var(--event-min-width, 0px))`,
    top: `${body?.scrollTop ?? 0}px`,
    height: `${body?.clientHeight ?? 0}px`
  };
});

const virtualizer = useVirtualizer(
  computed(() => ({
    count: rowCount.value,
    getScrollElement: () => parentRef.value,
    estimateSize: () => rowHeight.value,
    overscan: VIRTUAL_OVERSCAN,
    enabled: !usesScaledScroll.value
  }))
);

const spacerHeight = computed(() => {
  if (rowCount.value <= 0) {
    return 0;
  }
  if (usesScaledScroll.value) {
    return MAX_SCROLL_CONTENT_PX;
  }
  if (props.total == null) {
    return virtualizer.value.getTotalSize();
  }
  return linearContentHeight.value;
});

const visibleSlots = computed(() => {
  void scrollTick.value;
  void props.revision;
  if (rowCount.value <= 0) {
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
  const exact = exactIndexFromScrollTop(body.scrollTop);
  const viewportRows = Math.ceil(body.clientHeight / rowHeight.value);
  const from = Math.max(0, Math.floor(exact) - VIRTUAL_OVERSCAN);
  const to = Math.min(rowCount.value - 1, Math.ceil(exact + viewportRows) + VIRTUAL_OVERSCAN);
  const slots: { index: number; start?: number; localY?: number }[] = [];
  for (let index = from; index <= to; index += 1) {
    slots.push({ index, localY: (index - exact) * rowHeight.value });
  }
  return slots;
});

watch(visibleSlots, () => {
  if (props.total == null) {
    return;
  }
  const slots = visibleSlots.value;
  if (slots.length === 0) {
    emit("range", 0, -1);
    return;
  }
  emit("range", slots[0].index, slots[slots.length - 1].index);
});

function rowAt(index: number): EventLogItem | undefined {
  if (props.total != null && props.getRow) {
    return props.getRow(index);
  }
  return props.items[index];
}

function rowIdentity(index: number): string {
  const row = rowAt(index);
  if (!row) {
    return "";
  }
  return `${row.time}-${row.correlationId}-${row.alarmState}`;
}

function rowKey(index: number): string {
  return rowIdentity(index) || `row-${index}`;
}

function rowClass(index: number): Record<string, boolean> {
  const row = rowAt(index);
  return {
    selected: index === props.selectedIndex,
    placeholder: props.total != null && !row,
    [`state-${stateClass(row?.alarmState)}`]: true,
    "has-server-color": hasServerColor(row),
    "source-offline": isRowOffline(row)
  };
}

function slotStyle(
  slot: { start?: number; localY?: number },
  row: EventLogItem | undefined
): Record<string, string> {
  return rowStyle(slot.localY ?? slot.start ?? 0, row);
}

function clampIndex(index: number, total: number): number {
  if (total <= 0) {
    return 0;
  }
  return Math.min(total - 1, Math.max(0, index));
}

function scaledScrollMetrics(body: HTMLElement): { maxScroll: number; maxIndex: number } {
  const maxScroll = Math.max(0, body.scrollHeight - body.clientHeight);
  const viewportRows = rowHeight.value > 0 ? body.clientHeight / rowHeight.value : 0;
  const maxIndex = Math.max(0, rowCount.value - viewportRows);
  return { maxScroll, maxIndex };
}

function exactIndexFromScrollTop(scrollTop: number): number {
  if (rowCount.value <= 1) {
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

function syncScrollbarWidth() {
  const body = parentRef.value;
  if (!body) {
    return;
  }
  scrollbarWidth.value = Math.max(0, body.offsetWidth - body.clientWidth);
}

function formatTime(value: string | undefined): string {
  if (!value) {
    return "";
  }

  const date = new Date(value);
  return showTimeMilliseconds.value ? formatDateTimeMs(date) : formatDateTime(date);
}

function limitOf(row: EventLogItem | undefined): string {
  return limitStateFromProps(row?.props);
}

function stateLabel(row: EventLogItem | undefined): string {
  return formatAlarmState(row?.alarmState, limitOf(row));
}

function stateClass(state: string | undefined): string {
  switch (alarmStateBase(state)) {
    case "Incoming":
    case "ActiveUnacked":
    case "ActiveAcked":
      return "active-unacked";
    case "Incoming/Outgoing":
    case "InactiveUnacked":
      return "inactive-unacked";
    case "Removed":
    case "Normal":
      return "inactive";
    case "Disabled":
    case "OutOfService":
      return "disabled";
    case "Suppressed":
    case "Shelved":
    case "SuppressedOrShelved":
      return "shelved";
    case "Inactive":
      return "inactive";
    default:
      return "inactive";
  }
}

function formatSeverity(value: number | null | undefined): string {
  if (value == null) {
    return "—";
  }

  return formatInteger(value);
}

function formatDuration(row: EventLogItem | undefined): string {
  if (!row) {
    return "";
  }

  return formatDurationMs(row.durationMs);
}

function parseDsName(propsJson: string | undefined): string | null {
  if (!propsJson) {
    return null;
  }

  try {
    const parsed = JSON.parse(propsJson) as { DsName?: string };
    return parsed.DsName?.trim() || null;
  } catch {
    return null;
  }
}

function sourceLabel(row: EventLogItem | undefined): string {
  if (!row) {
    return "";
  }

  if (row.sourceId > 0) {
    return props.sourceNameById[row.sourceId] ?? parseDsName(row.props) ?? `Source ${row.sourceId}`;
  }

  return parseDsName(row.props) ?? "";
}

function sourceTitle(row: EventLogItem | undefined): string {
  if (!row) {
    return "";
  }

  const name = sourceLabel(row);
  const type = row.sourceType ? ` · ${row.sourceType}` : "";
  return `${name}${type}`;
}

function classLabel(row: EventLogItem | undefined): string {
  return alarmClassLabel(row?.alarmClassSymbol, row?.alarmClassName);
}

function classTitle(row: EventLogItem | undefined): string {
  const symbol = row?.alarmClassSymbol?.trim() ?? "";
  const name = formatClassName(row?.alarmClassName);
  if (symbol && name && symbol !== name) {
    return name;
  }
  return classLabel(row);
}

function typeLabel(row: EventLogItem | undefined): string {
  return row?.eventTypeName?.trim() || row?.eventType?.trim() || "";
}

function hasServerColor(row: EventLogItem | undefined): boolean {
  return row?.textColor != null && row?.backColor != null;
}

function isSourceOffline(row: EventLogItem | undefined): boolean {
  return row != null && props.offlineSourceIds?.has(row.sourceId) === true;
}

function isRowOffline(row: EventLogItem | undefined): boolean {
  return isSourceOffline(row) || (props.updatesStopped && row != null);
}

function rowOfflineTitle(row: EventLogItem | undefined): string | undefined {
  if (isSourceOffline(row)) {
    return "Source disconnected";
  }

  if (props.updatesStopped && row != null) {
    return "Updates stopped";
  }

  return undefined;
}

function alarmColorCss(value: number): string {
  // WinCC sends 0xAARRGGBB. The high byte is opaque. The strip stays this color.
  const color = value >>> 0;
  const red = (color >>> 16) & 255;
  const green = (color >>> 8) & 255;
  const blue = color & 255;
  return `rgb(${red} ${green} ${blue})`;
}

function rowStyle(start: number, row: EventLogItem | undefined): Record<string, string> {
  const style: Record<string, string> = {
    transform: `translateY(${start}px)`
  };
  if (row?.textColor == null || row?.backColor == null) {
    return style;
  }
  style["--alarm-back"] = alarmColorCss(row.backColor);
  return style;
}

function preview(message: string | undefined): string {
  if (!message) {
    return "";
  }

  const singleLine = message.replace(/\s+/g, " ").trim();
  return singleLine.length > 240 ? `${singleLine.slice(0, 240)}…` : singleLine;
}

function cellClass(id: AlarmColumnId): string {
  switch (id) {
    case "time":
      return "col-time";
    case "state":
      return "col-state-label";
    case "severity":
      return "col-severity";
    case "active":
    case "acked":
    case "retain":
      return "col-flag";
    case "duration":
      return "col-duration";
    case "source":
      return "col-source";
    case "class":
      return "col-class";
    case "type":
      return "col-type";
    case "message":
      return "col-message";
    case "exception":
      return "col-message";
  }
}

function cellText(id: AlarmColumnId, row: EventLogItem | undefined): string {
  if (!row) {
    return id === "message" ? "…" : "";
  }
  switch (id) {
    case "time":
      return formatTime(row?.time);
    case "state":
      return stateLabel(row);
    case "severity":
      return formatSeverity(row?.severity);
    case "active":
      return formatYesNo(row?.active);
    case "acked":
      return formatYesNo(row?.acked);
    case "retain":
      return formatYesNo(row?.retain);
    case "duration":
      return formatDuration(row);
    case "source":
      return sourceLabel(row);
    case "class":
      return classLabel(row);
    case "type":
      return typeLabel(row);
    case "message":
      return preview(row?.message);
    case "exception":
      return preview(row?.exception);
  }
}

function cellTitle(id: AlarmColumnId, row: EventLogItem | undefined): string {
  switch (id) {
    case "source":
      return sourceTitle(row);
    case "class":
      return classTitle(row);
    case "type":
      return row?.eventType?.trim() || typeLabel(row);
    case "message":
      return row?.message ?? "";
    case "exception":
      return row?.exception ?? "";
    default:
      return cellText(id, row);
  }
}

function ariaSort(id: AlarmColumnId): "none" | "ascending" | "descending" {
  if (!props.sortable || props.sortColumn !== id) {
    return "none";
  }

  return props.sortDirection === "desc" ? "descending" : "ascending";
}

function sortMark(id: AlarmColumnId): string {
  if (!props.sortable || props.sortColumn !== id) {
    return "";
  }

  return props.sortDirection === "desc" ? "↓" : "↑";
}

function sortTitle(id: AlarmColumnId, label: string): string | undefined {
  if (!props.sortable) {
    return undefined;
  }

  if (props.sortColumn !== id) {
    return `Sort by ${label}`;
  }

  return props.sortDirection === "desc"
    ? "Sorted descending. Click to clear sorting."
    : "Sorted ascending. Click to sort descending.";
}

function headClass(id: AlarmColumnId): Record<string, boolean> {
  const over = dragOverId.value === id && draggingId.value !== null && draggingId.value !== id;
  return {
    "is-drag-source": draggingId.value === id,
    "drop-before": over && !dragOverAfter.value,
    "drop-after": over && dragOverAfter.value
  };
}

function isVisible(id: AlarmColumnId): boolean {
  return columns.value.some((column) => column.id === id && column.visible);
}

function isOnlyVisible(id: AlarmColumnId): boolean {
  const visible = columns.value.filter((column) => column.visible);
  return visible.length === 1 && visible[0]?.id === id;
}

function resetColumns() {
  columns.value = defaultColumns();
  showTimeMilliseconds.value = false;
  writeColumns();
  emit("reset");
}

function onMillisecondsChange(event: Event) {
  const input = event.target as HTMLInputElement;
  showTimeMilliseconds.value = input.checked;
  writeColumns();
}

function onShowChange(id: AlarmColumnId, event: Event) {
  const input = event.target as HTMLInputElement;
  const column = columns.value.find((item) => item.id === id);
  if (!column) {
    return;
  }
  if (!input.checked && isOnlyVisible(id)) {
    input.checked = true;
    return;
  }
  column.visible = input.checked;
  writeColumns();
}

function toggleOptions() {
  optionsOpen.value = !optionsOpen.value;
  if (optionsOpen.value) {
    placeOptionsMenu();
  }
}

function closeOptions() {
  optionsOpen.value = false;
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

function setDragCursor(cursor: string) {
  document.body.style.cursor = cursor;
  document.body.style.userSelect = "none";
}

function clearDragCursor() {
  document.body.style.cursor = "";
  document.body.style.userSelect = "";
}

function onResizePointerDown(event: PointerEvent, id: AlarmColumnId) {
  if (event.button !== 0) {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  const column = columns.value.find((item) => item.id === id);
  if (!column) {
    return;
  }
  const handle = event.currentTarget as HTMLElement;
  try {
    handle.setPointerCapture(event.pointerId);
  } catch {
    // The pointer can already be gone. Moves still update the width.
  }
  resizeDrag = { id, startX: event.clientX, startWidth: column.width };
  setDragCursor("col-resize");
}

function onResizePointerMove(event: PointerEvent) {
  if (!resizeDrag) {
    return;
  }
  const column = columns.value.find((item) => item.id === resizeDrag?.id);
  if (!column) {
    return;
  }
  const def = defById(resizeDrag.id);
  const next = Math.min(
    MAX_COLUMN_PX,
    Math.max(def.minWidth, Math.round(resizeDrag.startWidth + event.clientX - resizeDrag.startX))
  );
  if (column.width !== next) {
    column.width = next;
  }
}

function onResizePointerUp() {
  if (!resizeDrag) {
    return;
  }
  resizeDrag = null;
  clearDragCursor();
  writeColumns();
}

function contentWidth(element: Element): number {
  const range = document.createRange();
  range.selectNodeContents(element);
  return range.getBoundingClientRect().width;
}

function horizontalPadding(element: Element): number {
  const style = window.getComputedStyle(element);
  return (parseFloat(style.paddingLeft) || 0) + (parseFloat(style.paddingRight) || 0);
}

/** Fits a column to its header and to the rows inside the viewport. Overscan rows do not count. */
function autoFitColumn(id: AlarmColumnId) {
  resizeDrag = null;
  clearDragCursor();
  const column = columns.value.find((item) => item.id === id);
  const position = visibleColumns.value.findIndex((item) => item.id === id);
  if (!column || position < 0) {
    return;
  }

  let widest = 0;
  const head = headerRef.value?.querySelector(`[data-column-id="${id}"]`);
  const label = head?.querySelector(".event-col-label");
  if (head && label) {
    widest = contentWidth(label) + horizontalPadding(head);
  }

  const body = parentRef.value;
  if (body) {
    const view = body.getBoundingClientRect();
    for (const row of body.querySelectorAll<HTMLElement>(".virtual-event-row:not(.placeholder)")) {
      const rect = row.getBoundingClientRect();
      if (rect.bottom <= view.top || rect.top >= view.bottom) {
        continue;
      }
      const cell = row.children[position];
      if (cell) {
        widest = Math.max(widest, contentWidth(cell) + horizontalPadding(cell));
      }
    }
  }

  if (widest <= 0) {
    return;
  }
  const def = defById(id);
  column.width = Math.min(MAX_COLUMN_PX, Math.max(def.minWidth, Math.ceil(widest) + 2));
  writeColumns();
}

function onHeaderPointerDown(event: PointerEvent, id: AlarmColumnId) {
  if (event.button !== 0) {
    return;
  }
  moveDrag = {
    id,
    startX: event.clientX,
    startY: event.clientY,
    pointerId: event.pointerId,
    active: false
  };
  window.addEventListener("pointermove", onHeaderPointerMove);
  window.addEventListener("pointerup", onHeaderPointerUp);
  window.addEventListener("pointercancel", onHeaderPointerUp);
}

function columnAtPoint(clientX: number): { id: AlarmColumnId; after: boolean } | null {
  const header = headerRef.value;
  if (!header) {
    return null;
  }
  const cells = [...header.querySelectorAll<HTMLElement>("[data-column-id]")];
  let nearest: HTMLElement | null = null;
  let nearestDistance = Number.POSITIVE_INFINITY;
  for (const cell of cells) {
    const rect = cell.getBoundingClientRect();
    if (clientX >= rect.left && clientX <= rect.right) {
      return {
        id: cell.dataset.columnId as AlarmColumnId,
        after: clientX > rect.left + rect.width / 2
      };
    }
    const mid = rect.left + rect.width / 2;
    const distance = Math.abs(clientX - mid);
    if (distance < nearestDistance) {
      nearestDistance = distance;
      nearest = cell;
    }
  }
  if (!nearest?.dataset.columnId || !isAlarmColumnId(nearest.dataset.columnId)) {
    return null;
  }
  const rect = nearest.getBoundingClientRect();
  return {
    id: nearest.dataset.columnId,
    after: clientX > rect.left + rect.width / 2
  };
}

function onHeaderPointerMove(event: PointerEvent) {
  if (!moveDrag || event.pointerId !== moveDrag.pointerId) {
    return;
  }
  if (!moveDrag.active) {
    const distance = Math.hypot(event.clientX - moveDrag.startX, event.clientY - moveDrag.startY);
    if (distance < DRAG_THRESHOLD_PX) {
      return;
    }
    moveDrag.active = true;
    draggingId.value = moveDrag.id;
    closeOptions();
    setDragCursor("grabbing");
  }
  const hit = columnAtPoint(event.clientX);
  dragOverId.value = hit?.id ?? null;
  dragOverAfter.value = hit?.after ?? false;
}

function reorderColumn(fromId: AlarmColumnId, toId: AlarmColumnId, after: boolean) {
  const next = columns.value.slice();
  const fromIndex = next.findIndex((column) => column.id === fromId);
  if (fromIndex < 0) {
    return;
  }
  const [item] = next.splice(fromIndex, 1);
  let toIndex = next.findIndex((column) => column.id === toId);
  if (toIndex < 0 || !item) {
    return;
  }
  if (after) {
    toIndex += 1;
  }
  next.splice(toIndex, 0, item);
  columns.value = next;
}

function onHeaderPointerUp(event: PointerEvent) {
  if (!moveDrag || event.pointerId !== moveDrag.pointerId) {
    return;
  }
  const drag = moveDrag;
  const overId = dragOverId.value;
  const after = dragOverAfter.value;
  const clickedLabel = event.target instanceof Element && event.target.closest(".event-col-label") !== null;
  endHeaderDrag();
  if (drag.active && overId && overId !== drag.id) {
    reorderColumn(drag.id, overId, after);
    writeColumns();
    return;
  }

  if (!drag.active && clickedLabel && props.sortable) {
    emit("sort", drag.id);
  }
}

function endHeaderDrag() {
  window.removeEventListener("pointermove", onHeaderPointerMove);
  window.removeEventListener("pointerup", onHeaderPointerUp);
  window.removeEventListener("pointercancel", onHeaderPointerUp);
  moveDrag = null;
  draggingId.value = null;
  dragOverId.value = null;
  clearDragCursor();
}

function onScroll() {
  closeOptions();
  scrollTick.value += 1;
  syncScrollbarWidth();
  // The body scrolls in both directions (so its scrollbars stay at the edge of the table), the header follows sideways
  if (headerScrollRef.value && parentRef.value && headerScrollRef.value.scrollLeft !== parentRef.value.scrollLeft) {
    headerScrollRef.value.scrollLeft = parentRef.value.scrollLeft;
  }
}

const ROW_MENU_MARGIN = 4;
// The menu opens a little up and left of the mouse, so the mouse starts inside of it: it closes when the mouse leaves
const ROW_MENU_INSET = 6;

type RowMenu = {
  left: number;
  top: number;
  sourceId: number;
  sourceName: string;
  classLabel: string;
  state: string;
  stateLabel: string;
};

const rowMenu = ref<RowMenu | null>(null);
const rowMenuRef = ref<HTMLElement | null>(null);

// The entries are there only for a column that is shown; without one the browser keeps its own menu
function onRowContextMenu(event: MouseEvent, index: number) {
  const row = rowAt(index);
  if (!row || !(isVisible("source") || isVisible("class") || (isVisible("state") && props.stateFilterOf))) {
    return;
  }
  event.preventDefault();
  focusTable();
  rowMenu.value = {
    left: event.clientX - ROW_MENU_INSET,
    top: event.clientY - ROW_MENU_INSET,
    sourceId: row.sourceId > 0 ? row.sourceId : 0,
    sourceName: sourceLabel(row),
    classLabel: alarmClassLabel(row.alarmClassSymbol, row.alarmClassName),
    state: props.stateFilterOf?.(row) ?? "",
    stateLabel: stateLabel(row)
  };
  void nextTick(() => {
    const menu = rowMenuRef.value;
    const current = rowMenu.value;
    if (!menu || !current) {
      return;
    }
    const { width, height } = menu.getBoundingClientRect();
    current.left = Math.max(ROW_MENU_MARGIN, Math.min(current.left, window.innerWidth - width - ROW_MENU_MARGIN));
    current.top = Math.max(ROW_MENU_MARGIN, Math.min(current.top, window.innerHeight - height - ROW_MENU_MARGIN));
    menu.focus();
  });
}

function closeRowMenu() {
  rowMenu.value = null;
}

function filterBySource() {
  const menu = rowMenu.value;
  closeRowMenu();
  if (menu?.sourceId) {
    emit("filter-source", menu.sourceId);
  }
}

function filterByClass() {
  const menu = rowMenu.value;
  closeRowMenu();
  if (menu?.classLabel) {
    emit("filter-class", menu.classLabel);
  }
}

function filterByState() {
  const menu = rowMenu.value;
  closeRowMenu();
  if (menu?.state) {
    emit("filter-state", menu.state);
  }
}

function onRowClick(index: number) {
  focusTable();
  emit("select", index);
}

function focusTable() {
  (parentRef.value?.closest(".virtual-event-table") as HTMLElement | null)?.focus({ preventScroll: true });
}

/**
 * Where the row stands in the table, in rows below its upper edge (negative above it); null when the table is not
 * there. The scroll position of a long list is not linear in pixels, so it is the row that is counted.
 */
function rowOffsetInView(index: number): number | null {
  const body = parentRef.value;
  if (!body || rowHeight.value <= 0) {
    return null;
  }
  const top = usesScaledScroll.value ? exactIndexFromScrollTop(body.scrollTop) : body.scrollTop / rowHeight.value;
  return index - top;
}

/**
 * Scrolls the row into view. With `place` it stands that many rows below the upper edge of the table (kept inside of
 * it), or in the middle with "center", even when it was in view.
 */
function scrollToIndex(index: number, place?: number | "center") {
  const body = parentRef.value;
  if (!body) {
    return;
  }
  if (place === undefined) {
    if (!usesScaledScroll.value) {
      virtualizer.value.scrollToIndex(index, { align: "auto" });
      return;
    }
    const { maxScroll, maxIndex } = scaledScrollMetrics(body);
    body.scrollTop = maxIndex <= 0 ? 0 : (clampIndex(index, rowCount.value) / maxIndex) * maxScroll;
    return;
  }

  const lastRowInView = Math.max(0, body.clientHeight / Math.max(1, rowHeight.value) - 1);
  const offset = place === "center" ? lastRowInView / 2 : Math.min(Math.max(place, 0), lastRowInView);
  const top = index - offset;
  if (!usesScaledScroll.value) {
    body.scrollTop = Math.max(0, top * rowHeight.value);
    return;
  }
  const { maxScroll, maxIndex } = scaledScrollMetrics(body);
  body.scrollTop = maxIndex <= 0 ? 0 : (Math.min(Math.max(top, 0), maxIndex) / maxIndex) * maxScroll;
}

defineExpose({ scrollToIndex, rowOffsetInView });

watch(rowHeight, () => {
  virtualizer.value.measure();
});

let bodyResizeObserver: ResizeObserver | null = null;

onMounted(() => {
  syncRowHeight();
  if (parentRef.value && typeof ResizeObserver !== "undefined") {
    // The scaled viewport derives its rows from the body height. A resize must recompute them.
    bodyResizeObserver = new ResizeObserver(() => {
      scrollTick.value += 1;
      syncScrollbarWidth();
    });
    bodyResizeObserver.observe(parentRef.value);
  }
  window.addEventListener("pointerdown", onOptionsPointerDown);
});

onUnmounted(() => {
  bodyResizeObserver?.disconnect();
  bodyResizeObserver = null;
  endHeaderDrag();
  resizeDrag = null;
  clearDragCursor();
  window.removeEventListener("pointerdown", onOptionsPointerDown);
});
</script>
