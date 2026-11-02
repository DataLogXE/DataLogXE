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
    <div class="page-header">
      <h1>Data sources</h1>
      <div class="page-header-actions">
        <router-link v-if="isAdmin" class="btn" to="/datasources/new">New source</router-link>
        <router-link class="btn secondary" to="/">Back</router-link>
      </div>
    </div>

    <div class="page-body">
    <p v-if="pollError" class="error-text">{{ pollError }}</p>

    <div class="source-table-shell">
    <div class="table-wrap">
      <table v-if="sources.length" ref="tableRef" class="source-list-table" :style="tableStyle">
        <colgroup>
          <col v-for="column in visibleColumns" :key="column.id" :style="{ width: `${column.width}px` }" />
          <col class="col-actions" :style="{ width: actionsWidth }" />
        </colgroup>
        <thead>
          <tr>
            <th
              v-for="column in visibleColumns"
              :key="column.id"
              :class="`col-${column.id}`"
              :data-column-id="column.id"
              :aria-sort="ariaSort(column.id)"
            >
              <span class="source-col-label" :data-hint="sortTitle(column.id, column.label)" @click="toggleSort(column.id)">
                <span class="event-col-text">{{ column.label }}</span>
                <span v-if="sortColumn === column.id" class="event-sort-mark" aria-hidden="true">
                  {{ sortDir === "desc" ? "↓" : "↑" }}
                </span>
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
            </th>
            <th class="col-actions">
              <span class="sr-only">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          <tr
            v-for="source in sortedSources"
            :key="source.id"
            @contextmenu.prevent="openMenu(source, $event)"
            @dblclick="router.push(`/datasources/${source.id}/tags`)"
          >
            <template v-for="column in visibleColumns" :key="column.id">
              <td v-if="column.id === 'name'" class="col-name" :data-hint="source.name">{{ source.name }}</td>
              <td v-else-if="column.id === 'type'" class="col-type">
                <span class="source-type">
                  <SourceTypeIcon :type="source.type" />
                  <span class="source-type-text">{{ typeLabel(source.type) }}</span>
                </span>
              </td>
              <td v-else-if="column.id === 'status'" class="col-status">
                <div class="status-badges">
                  <span class="badge" :class="badgeClass(source.state)">{{ source.state }}</span>
                  <span
                    v-if="source.isActive && source.type === 'OpcUa' && source.opcUaSecurityMode === 'none'"
                    class="badge connecting"
                    data-hint="Unsecured OPC UA connection: values and the password travel in plain text."
                  >Unsecured</span>
                  <component
                    :is="isAdmin ? 'router-link' : 'span'"
                    v-if="source.opcUaCertificateRejected"
                    class="badge error"
                    :to="isAdmin ? `/datasources/${source.id}` : undefined"
                    data-hint="The server shows another certificate than the trusted one, so DataLogXE does not connect. Open the source and run the connection test to check and trust the new certificate."
                  >Certificate changed</component>
                  <span v-if="source.tagBrowseActive" class="badge connecting">TagBrowse</span>
                  <span v-if="source.tagImportActive" class="badge connecting">TagImport</span>
                </div>
              </td>
              <td v-else-if="column.id === 'host'" class="col-host" :class="{ 'is-empty': !source.host }" :data-hint="hostText(source)">{{ hostText(source) }}</td>
              <td v-else class="col-throughput">
                <ThroughputSparkline
                  :values="source.valuesPerSecSeries ?? []"
                  :current="source.valuesPerSec"
                />
              </td>
            </template>
            <td class="col-actions">
              <div class="actions">
                <router-link
                  v-if="isAdmin"
                  class="icon-btn"
                  :to="`/datasources/${source.id}`"
                  data-hint="Edit"
                  aria-label="Edit data source"
                >
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M12.5 20.5h8" />
                    <path d="M16.7 4.3a2.1 2.1 0 0 1 3 3L8 19l-4 1 1-4 11.7-11.7z" />
                  </svg>
                </router-link>
                <router-link
                  class="icon-btn"
                  :to="`/datasources/${source.id}/tags`"
                  data-hint="Data tags"
                  aria-label="Show data tags"
                >
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M20.5 13.4l-7.2 7.2a2 2 0 0 1-2.8 0L2.5 12.6V3.5h9.1l8.9 8.9a2 2 0 0 1 0 2.8z" />
                    <circle cx="8" cy="8.2" r="1.35" />
                  </svg>
                </router-link>
                <router-link
                  class="icon-btn"
                  :to="`/datasources/${source.id}/log`"
                  data-hint="Log"
                  aria-label="Show log"
                >
                  <svg viewBox="0 0 24 24" aria-hidden="true">
                    <path d="M6 3.5h9l4 4v13H6z" />
                    <path d="M9.5 11h6M9.5 14.5h6M9.5 18h4" />
                  </svg>
                </router-link>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else class="empty">No data sources yet.</div>
    </div>
    <div v-if="sources.length" class="source-col-options">
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
    </div>
    </div>

    <Teleport to="body">
      <div
        v-if="menu"
        class="tag-context-menu-backdrop"
        @click="closeMenu"
        @contextmenu.prevent="closeMenu"
      >
        <div
          ref="menuRef"
          class="tag-context-menu"
          role="menu"
          :style="{ left: `${menu.x}px`, top: `${menu.y}px` }"
          @click.stop
        >
          <button v-if="isAdmin" type="button" role="menuitem" @click="openFromMenu(`/datasources/${menu.source.id}`)">
            Edit
          </button>
          <button type="button" role="menuitem" @click="openFromMenu(`/datasources/${menu.source.id}/tags`)">
            Data tags
          </button>
          <button type="button" role="menuitem" @click="openFromMenu(`/datasources/${menu.source.id}/log`)">
            Log
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
        <label v-for="column in SOURCE_COLUMNS" :key="column.id">
          <input
            type="checkbox"
            :checked="isVisible(column.id)"
            :disabled="isOnlyVisible(column.id)"
            @change="onShowChange(column.id, $event)"
          />
          Show {{ column.label }}
        </label>
      </div>
    </Teleport>

    <WaitOverlay
      :visible="busy"
      :title="overlayTitle"
      :message="overlayMessage"
      :error="overlayError"
      :cancellable="true"
      @cancel="cancelCurrent"
    />
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useRouter } from "vue-router";
import SourceTypeIcon from "../components/SourceTypeIcon.vue";
import ThroughputSparkline from "../components/ThroughputSparkline.vue";
import WaitOverlay from "../components/WaitOverlay.vue";
import { createPollSession, getDataSources, isAbortError } from "../api";
import { isAdmin } from "../session";
import { badgeClass, typeLabel } from "../sourceState";
import type { DataSourceListItem } from "../types";

// The columns of the list, their widths and which are shown are kept in the browser, like the columns of the alarms
const COLUMN_PREFS_KEY = "dataSourceColumns";
const MAX_COLUMN_PX = 1200;

type SourceColumnId = "name" | "type" | "status" | "host" | "throughput";

type SourceColumnDef = {
  id: SourceColumnId;
  label: string;
  defaultWidth: number;
  minWidth: number;
};

type SortDir = "asc" | "desc";

type StoredPrefs = {
  widths?: Record<string, unknown>;
  visible?: Record<string, unknown>;
  sort?: { column?: unknown; dir?: unknown };
};

type SourceColumnState = {
  id: SourceColumnId;
  width: number;
  visible: boolean;
};

const SOURCE_COLUMNS: SourceColumnDef[] = [
  { id: "name", label: "Name", defaultWidth: 176, minWidth: 80 },
  { id: "type", label: "Type", defaultWidth: 164, minWidth: 56 },
  { id: "status", label: "Status", defaultWidth: 258, minWidth: 80 },
  { id: "host", label: "Host", defaultWidth: 240, minWidth: 64 },
  { id: "throughput", label: "Values / s", defaultWidth: 164, minWidth: 96 }
];

function defaultColumns(): SourceColumnState[] {
  return SOURCE_COLUMNS.map((column) => ({ id: column.id, width: column.defaultWidth, visible: true }));
}

function readColumns(): SourceColumnState[] {
  try {
    const raw = window.localStorage.getItem(COLUMN_PREFS_KEY);
    if (!raw) {
      return defaultColumns();
    }
    const parsed = JSON.parse(raw) as StoredPrefs;
    const columns = SOURCE_COLUMNS.map((def) => {
      const widthRaw = parsed.widths?.[def.id];
      const visibleRaw = parsed.visible?.[def.id];
      return {
        id: def.id,
        width: typeof widthRaw === "number" && Number.isFinite(widthRaw)
          ? Math.min(MAX_COLUMN_PX, Math.max(def.minWidth, Math.round(widthRaw)))
          : def.defaultWidth,
        visible: typeof visibleRaw === "boolean" ? visibleRaw : true
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

function readSort(): { column: SourceColumnId | null; dir: SortDir } {
  try {
    const raw = window.localStorage.getItem(COLUMN_PREFS_KEY);
    const parsed = raw ? (JSON.parse(raw) as StoredPrefs) : {};
    const column = SOURCE_COLUMNS.find((def) => def.id === parsed.sort?.column)?.id ?? null;
    return { column, dir: parsed.sort?.dir === "desc" ? "desc" : "asc" };
  } catch {
    return { column: null, dir: "asc" };
  }
}

function writeColumns() {
  try {
    window.localStorage.setItem(COLUMN_PREFS_KEY, JSON.stringify({
      widths: Object.fromEntries(columns.value.map((column) => [column.id, column.width])),
      visible: Object.fromEntries(columns.value.map((column) => [column.id, column.visible])),
      sort: { column: sortColumn.value, dir: sortDir.value }
    }));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

function defById(id: SourceColumnId): SourceColumnDef {
  return SOURCE_COLUMNS.find((column) => column.id === id) ?? SOURCE_COLUMNS[0];
}

const columns = ref<SourceColumnState[]>(readColumns());
// A click on a header sorts ascending, then descending, then back to the order of the server
const initialSort = readSort();
const sortColumn = ref<SourceColumnId | null>(initialSort.column);
const sortDir = ref<SortDir>(initialSort.dir);
const tableRef = ref<HTMLTableElement | null>(null);
const optionsBtn = ref<HTMLButtonElement | null>(null);
const optionsOpen = ref(false);
const menuStyle = ref<Record<string, string>>({});

const visibleColumns = computed(() =>
  columns.value
    .filter((column) => column.visible)
    .map((column) => ({ ...defById(column.id), width: column.width }))
);

const columnsWidth = computed(() => visibleColumns.value.reduce((total, column) => total + column.width, 0));

// The actions column takes the rest of the width (a fixed table would stretch every column instead); narrower than
// the columns the table scrolls sideways
const tableStyle = computed(() => ({ minWidth: `calc(${columnsWidth.value}px + var(--source-actions-width))` }));
// Percentages do not work for a column of a fixed table, so the width of the card is measured
const wrapWidth = ref(0);
const actionsWidth = computed(() => `max(var(--source-actions-width), ${wrapWidth.value - columnsWidth.value}px)`);
let wrapObserver: ResizeObserver | null = null;

watch(tableRef, (table) => {
  wrapObserver?.disconnect();
  const wrap = table?.parentElement;
  if (!wrap || typeof ResizeObserver === "undefined") {
    return;
  }
  wrapObserver = new ResizeObserver(() => {
    wrapWidth.value = wrap.clientWidth;
  });
  wrapObserver.observe(wrap);
});

function isVisible(id: SourceColumnId): boolean {
  return columns.value.some((column) => column.id === id && column.visible);
}

function isOnlyVisible(id: SourceColumnId): boolean {
  const visible = columns.value.filter((column) => column.visible);
  return visible.length === 1 && visible[0]?.id === id;
}

function onShowChange(id: SourceColumnId, event: Event) {
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

function toggleSort(id: SourceColumnId) {
  if (sortColumn.value !== id) {
    sortColumn.value = id;
    sortDir.value = "asc";
  } else if (sortDir.value === "asc") {
    sortDir.value = "desc";
  } else {
    sortColumn.value = null;
    sortDir.value = "asc";
  }
  writeColumns();
}

function ariaSort(id: SourceColumnId): "none" | "ascending" | "descending" {
  if (sortColumn.value !== id) {
    return "none";
  }
  return sortDir.value === "desc" ? "descending" : "ascending";
}

function sortTitle(id: SourceColumnId, label: string): string {
  if (sortColumn.value !== id) {
    return `Sort by ${label}`;
  }
  return sortDir.value === "desc"
    ? "Sorted descending. Click to clear sorting."
    : "Sorted ascending. Click to sort descending.";
}

function resetColumns() {
  columns.value = defaultColumns();
  writeColumns();
}

function toggleOptions() {
  optionsOpen.value = !optionsOpen.value;
  const button = optionsBtn.value;
  if (optionsOpen.value && button) {
    const rect = button.getBoundingClientRect();
    menuStyle.value = { top: `${Math.round(rect.bottom + 4)}px`, left: `${Math.round(rect.right)}px` };
  }
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

let resizeDrag: { id: SourceColumnId; startX: number; startWidth: number } | null = null;

function setDragCursor(cursor: string) {
  document.body.style.cursor = cursor;
  document.body.style.userSelect = "none";
}

function clearDragCursor() {
  document.body.style.cursor = "";
  document.body.style.userSelect = "";
}

function onResizePointerDown(event: PointerEvent, id: SourceColumnId) {
  if (event.button !== 0) {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  const column = columns.value.find((item) => item.id === id);
  if (!column) {
    return;
  }
  try {
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
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
  column.width = Math.min(
    MAX_COLUMN_PX,
    Math.max(def.minWidth, Math.round(resizeDrag.startWidth + event.clientX - resizeDrag.startX))
  );
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

/** Fits a column to its header and to all rows of the list. */
function autoFitColumn(id: SourceColumnId) {
  resizeDrag = null;
  clearDragCursor();
  const column = columns.value.find((item) => item.id === id);
  const position = visibleColumns.value.findIndex((item) => item.id === id);
  const table = tableRef.value;
  if (!column || position < 0 || !table) {
    return;
  }

  let widest = 0;
  const head = table.querySelector(`th[data-column-id="${id}"]`);
  const label = head?.querySelector(".source-col-label");
  if (head && label) {
    widest = contentWidth(label) + horizontalPadding(head);
  }
  for (const row of table.tBodies[0]?.rows ?? []) {
    const cell = row.cells[position];
    if (cell) {
      widest = Math.max(widest, contentWidth(cell) + horizontalPadding(cell));
    }
  }

  if (widest <= 0) {
    return;
  }
  const def = defById(id);
  column.width = Math.min(MAX_COLUMN_PX, Math.max(def.minWidth, Math.ceil(widest) + 2));
  writeColumns();
}

// Right-click on a row opens the same pages as the buttons of the row
const MENU_WIDTH = 176;
const MENU_HEIGHT = 80;
const router = useRouter();
const menu = ref<{ source: DataSourceListItem; x: number; y: number } | null>(null);
const menuRef = ref<HTMLDivElement | null>(null);

function openMenu(source: DataSourceListItem, event: MouseEvent) {
  optionsOpen.value = false;
  menu.value = {
    source,
    x: Math.max(4, Math.min(event.clientX, window.innerWidth - MENU_WIDTH - 4)),
    y: Math.max(4, Math.min(event.clientY, window.innerHeight - MENU_HEIGHT - 4))
  };
  void nextTick(() => menuRef.value?.querySelector<HTMLButtonElement>("button")?.focus());
}

function closeMenu() {
  menu.value = null;
}

function openFromMenu(path: string) {
  closeMenu();
  void router.push(path);
}

function onMenuKeyDown(event: KeyboardEvent) {
  if (event.key === "Escape" && menu.value) {
    closeMenu();
  }
}

const sources = ref<DataSourceListItem[]>([]);
const textCollator = new Intl.Collator(undefined, { numeric: true, sensitivity: "base" });

function sortText(source: DataSourceListItem, id: SourceColumnId): string {
  if (id === "type") return typeLabel(source.type);
  if (id === "status") return source.state;
  if (id === "host") return source.host ? hostText(source) : "";
  return source.name;
}

const sortedSources = computed(() => {
  const id = sortColumn.value;
  if (!id) {
    return sources.value;
  }
  const sign = sortDir.value === "desc" ? -1 : 1;
  return [...sources.value].sort((a, b) => {
    const order = id === "throughput"
      ? (a.valuesPerSec ?? -1) - (b.valuesPerSec ?? -1)
      : textCollator.compare(sortText(a, id), sortText(b, id));
    return sign * order || textCollator.compare(a.name, b.name) || a.id - b.id;
  });
});

const busy = ref(true);
const overlayError = ref("");
const overlayTitle = ref("Loading");
const overlayMessage = ref("Loading data sources …");
const pollError = ref("");
const poll = createPollSession();
let timer: number | undefined;
function hostText(source: DataSourceListItem): string {
  if (!source.host) {
    return "–";
  }
  return source.port ? `${source.host}:${source.port}` : source.host;
}

function cancelCurrent() {
  poll.abort();
  busy.value = false;
  overlayError.value = "The request was cancelled.";
}

async function load(initial: boolean) {
  const session = poll.begin();
  if (initial || sources.value.length === 0) {
    busy.value = true;
    overlayError.value = "";
    overlayTitle.value = "Loading";
    overlayMessage.value = "Loading data sources …";
  }

  try {
    const data = await getDataSources(session.signal);
    if (!session.isCurrent()) {
      return;
    }
    sources.value = data;
    pollError.value = "";
    overlayError.value = "";
    busy.value = false;
  } catch (error) {
    if (!session.isCurrent() || isAbortError(error)) {
      return;
    }
    const message = error instanceof Error ? error.message : "Unknown error";
    pollError.value = message;
    if (sources.value.length === 0) {
      overlayError.value = message;
      overlayTitle.value = "Error";
      busy.value = true;
    } else {
      busy.value = false;
    }
  }
}

onMounted(() => {
  window.addEventListener("pointerdown", onOptionsPointerDown);
  window.addEventListener("keydown", onMenuKeyDown);
  void load(true);
  timer = window.setInterval(() => {
    void load(false);
  }, 2000);
});

onUnmounted(() => {
  if (timer !== undefined) {
    window.clearInterval(timer);
  }
  poll.abort();
  resizeDrag = null;
  clearDragCursor();
  wrapObserver?.disconnect();
  window.removeEventListener("pointerdown", onOptionsPointerDown);
  window.removeEventListener("keydown", onMenuKeyDown);
});
</script>
