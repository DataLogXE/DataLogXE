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
    <div class="events-page">
      <div class="page-header">
        <h1>Pending</h1>
        <div v-if="summary" class="page-header-actions events-summary-badges">
          <span class="badge default">{{ summary.activeAlarms }} alarms</span>
          <span
            v-if="updateMs !== null"
            class="badge alarm-update"
            :class="{ 'is-slow': updateIsSlow && !updatesStale, 'is-stale': updatesStale }"
            :style="{ '--alarm-update-ms': `${ACTIVE_POLL_MS}ms` }"
            :data-hint="updateTitle"
            :aria-label="updateTitle"
          >
            <span :key="updateSeq" class="alarm-update-bar" aria-hidden="true" />
            <span class="alarm-update-label">{{ updateLabel }}</span>
          </span>
        </div>
      </div>

      <div class="events-toolbar">
        <div class="events-search tag-filter-field">
          <input
            v-model="searchText"
            type="text"
            placeholder="Message, class, or state"
            data-hint="Filters message, class, and state. Wildcards * and ? are supported."
            aria-label="Filter by message, class, or state"
            autocomplete="off"
            spellcheck="false"
          />
          <button
            v-if="searchText"
            class="tag-icon-btn field-clear"
            type="button"
            data-hint="Clear filter"
            aria-label="Clear filter"
            @click="searchText = ''"
          >
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M4 4l8 8M12 4L4 12" />
            </svg>
          </button>
        </div>
        <div class="events-chips" aria-label="Active filters">
          <span v-for="chip in filterChips" :key="chip.key" class="badge default filter-chip">
            <span class="filter-chip-kind">{{ chip.kind }}:</span> {{ chip.label }}
            <button
              v-if="chip.remove"
              type="button"
              class="filter-chip-x"
              :data-hint="`Remove ${chip.kind} ${chip.label}`"
              :aria-label="`Remove ${chip.kind} ${chip.label}`"
              @click="chip.remove"
            >
              <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4L4 12" /></svg>
            </button>
          </span>
        </div>
        <button
          type="button"
          class="btn secondary btn-sm btn-filters"
          :class="{ active: !filtersCollapsed }"
          :aria-expanded="!filtersCollapsed"
          @click="toggleFilters"
        >
          <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
            <path d="M4 5h16l-6 7.2V19l-4-2v-4.8L4 5Z" />
          </svg>
          Filters
        </button>
      </div>

      <div
        class="events-layout"
        :class="{ 'filters-collapsed': filtersCollapsed }"
        :style="{ '--events-filters-width': `${filtersWidth}px` }"
      >
        <section class="events-main">
          <div v-if="error" class="events-error">
            <p class="error-text">{{ error }}</p>
            <button type="button" class="btn secondary btn-sm" @click="reloadActive(false)">Retry</button>
          </div>

          <div class="events-table-shell">
            <VirtualEventTable
              ref="eventTableRef"
              storage-key="alarmColumnsPending"
              :items="visibleItems"
              :selected-index="selectedIndex"
              :source-name-by-id="sourceNameById"
              :offline-source-ids="offlineSourceIds"
              :updates-stopped="updatesStale"
              :empty-text="emptyText"
              sortable
              :sort-column="sortColumn"
              :sort-direction="sortDir"
              @select="onSelect"
              @sort="onSortColumn"
              @reset="onResetColumns"
              @filter-source="filterBySource"
              @filter-class="filterByClass"
            />
          </div>

          <AlarmDetailPanel
            storage-key="alarmDetailHeightPending"
            :item="selectedItem"
            :source-label="selectedItem ? sourceLabelFor(selectedItem, sourceNameById) : ''"
            :source-offline="selectedSourceOffline"
            :updates-stale="updatesStale"
            @close="closeDetails"
          />
        </section>

        <AlarmFiltersPanel
          v-model:width="filtersWidth"
          storage-key="alarmFiltersWidthPending"
          :collapsed="filtersCollapsed"
          @close="closeFilters"
        >
          <h2>Source</h2>
          <div class="events-source" ref="sourceMenuRef">
            <button
              type="button"
              class="events-select events-source-toggle"
              :aria-expanded="sourceMenuOpen"
              aria-haspopup="listbox"
              @click="toggleSourceMenu"
            >
              <span>{{ sourceFilterLabel }}</span>
            </button>
            <div v-show="sourceMenuOpen" class="events-source-menu" role="listbox" aria-label="Sources" aria-multiselectable="true">
              <label class="events-source-option">
                <input
                  type="checkbox"
                  :checked="selectedSourceIds.length === 0"
                  @change="selectAllSources"
                />
                <span>All</span>
              </label>
              <label v-for="source in alarmSources" :key="source.id" class="events-source-option">
                <input
                  type="checkbox"
                  :checked="selectedSourceIds.includes(source.id)"
                  @change="toggleSource(source.id)"
                />
                <span>{{ source.name }}</span>
              </label>
              <p v-if="alarmSources.length === 0" class="events-source-empty">No alarm sources available.</p>
            </div>
          </div>

          <h2>Class</h2>
          <div class="events-source" ref="classMenuRef">
            <button
              type="button"
              class="events-select events-source-toggle"
              :aria-expanded="classMenuOpen"
              aria-haspopup="listbox"
              @click="toggleClassMenu"
            >
              <span>{{ classFilterLabel }}</span>
            </button>
            <div v-show="classMenuOpen" class="events-source-menu" role="listbox" aria-label="Classes" aria-multiselectable="true">
              <label class="events-source-option">
                <input
                  type="checkbox"
                  :checked="selectedClassLabels.length === 0"
                  @change="selectAllClasses"
                />
                <span>All</span>
              </label>
              <label v-for="label in alarmClassOptions" :key="label" class="events-source-option">
                <input
                  type="checkbox"
                  :checked="selectedClassLabels.includes(label)"
                  @change="toggleClass(label)"
                />
                <span>{{ label }}</span>
              </label>
              <p v-if="alarmClassOptions.length === 0" class="events-source-empty">No alarm classes seen yet.</p>
            </div>
          </div>
        </AlarmFiltersPanel>
      </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import AlarmDetailPanel from "../components/AlarmDetailPanel.vue";
import AlarmFiltersPanel from "../components/AlarmFiltersPanel.vue";
import VirtualEventTable from "../components/VirtualEventTable.vue";
import { createPollSession, getActiveAlarms, getAlarmClasses, getDataSources, isCancelledError } from "../api";
import { parseSourceIds, queryText, readFiltersWidth, sourceLabelFor } from "../alarmCommon";
import { alarmClassLabel, formatAlarmState, formatClassName, limitStateFromProps } from "../format";
import { anyTextMatches, parseTextFilter, type TextFilter } from "../textFilter";
import { isDataSourceOffline } from "../tagDisplay";
import type { AlarmClassItem, DataSourceListItem, EventLogItem, EventLogSummary } from "../types";

const ACTIVE_POLL_MS = 3000;
const UPDATE_OK_MAX_MS = 2000;
const UPDATE_STALE_MS = 8000;
const SOURCE_STATE_POLL_MS = 5000;
const PENDING_PATH = "/alarms/pending";
// The filters, the width of the filters and the columns of this page are kept in the browser apart from the history
const ALARM_FILTERS_KEY = "alarmFiltersPending";
const FILTERS_WIDTH_KEY = "alarmFiltersWidthPending";
// The keys of the time before every page had its own
const LEGACY_ALARM_FILTERS_KEY = "alarmFilters";
const LEGACY_FILTERS_WIDTH_KEY = "alarmFiltersWidth";

const eventTableRef = ref<{
  scrollToIndex: (index: number, place?: number | "center") => void;
  rowOffsetInView: (index: number) => number | null;
} | null>(null);
const route = useRoute();
const router = useRouter();
const poll = createPollSession();

const sources = ref<DataSourceListItem[]>([]);
const items = ref<EventLogItem[]>([]);
const summary = ref<EventLogSummary | null>(null);
const updateMs = ref<number | null>(null);
const updateSeq = ref(0);
const updatesStale = ref(false);
const updateIsSlow = computed(() => updateMs.value !== null && updateMs.value > UPDATE_OK_MAX_MS);
const updateLabel = computed(() => {
  if (updatesStale.value) {
    return "Stale";
  }

  return updateIsSlow.value ? `${updateMs.value} ms` : "OK";
});
const updateTitle = computed(() => {
  if (updatesStale.value) {
    return `No successful refresh for more than ${UPDATE_STALE_MS / 1000} s. These alarms may no longer be current.`;
  }

  if (updateMs.value === null) {
    return "";
  }

  const timing = `Last update took ${updateMs.value} ms.`;
  if (updateIsSlow.value) {
    return `${timing} This update took longer than 2 s. The bar fills until the next refresh.`;
  }

  return `${timing} The bar fills until the next refresh.`;
});
const busy = ref(false);
const error = ref("");
const selectedIndex = ref(-1);
const selectedCorrelationId = ref("");
const searchText = ref("");
const selectedSourceIds = ref<number[]>([]);
const selectedClassLabels = ref<string[]>([]);
const alarmClasses = ref<AlarmClassItem[]>([]);
const sourceMenuOpen = ref(false);
const classMenuOpen = ref(false);
const sourceMenuRef = ref<HTMLElement | null>(null);
const classMenuRef = ref<HTMLElement | null>(null);
const filtersCollapsed = ref(true);
const filtersWidth = ref(readFiltersWidth(FILTERS_WIDTH_KEY, LEGACY_FILTERS_WIDTH_KEY));
const sortColumn = ref<string | null>(null);
const sortDir = ref<"asc" | "desc">("asc");

let activeTimer: number | undefined;
let staleTimer: number | undefined;
let sourceStateTimer: number | undefined;
// The route syncQuery wrote last.
let syncedRoutePath = "";
let sourceStateAbort: AbortController | undefined;

function alarmSortValue(
  column: string,
  row: EventLogItem,
  sourceNames: Record<number, string>
): string | number | null {
  switch (column) {
    case "time": {
      const time = Date.parse(row.time);
      return Number.isNaN(time) ? null : time;
    }
    case "severity":
      return row.severity ?? null;
    case "active":
      return row.active == null ? null : row.active ? 1 : 0;
    case "acked":
      return row.acked == null ? null : row.acked ? 1 : 0;
    case "retain":
      return row.retain == null ? null : row.retain ? 1 : 0;
    case "duration":
      return row.durationMs != null && row.durationMs > 0 ? row.durationMs : null;
    case "state":
      return formatAlarmState(row.alarmState, limitStateFromProps(row.props));
    case "source":
      if (row.sourceId > 0) {
        return sourceNames[row.sourceId] ?? "";
      }
      return "";
    case "class":
      return row.alarmClassSymbol?.trim() || formatClassName(row.alarmClassName);
    case "type":
      return row.eventTypeName?.trim() || row.eventType?.trim() || "";
    case "message":
      return row.message ?? "";
    default:
      return "";
  }
}

function compareAlarmRows(
  column: string,
  left: EventLogItem,
  right: EventLogItem,
  direction: number,
  sourceNames: Record<number, string>
): number {
  const leftValue = alarmSortValue(column, left, sourceNames);
  const rightValue = alarmSortValue(column, right, sourceNames);
  const leftEmpty = leftValue == null || leftValue === "";
  const rightEmpty = rightValue == null || rightValue === "";
  if (leftEmpty || rightEmpty) {
    if (leftEmpty && rightEmpty) {
      return 0;
    }
    return leftEmpty ? 1 : -1;
  }

  if (typeof leftValue === "number" && typeof rightValue === "number") {
    return (leftValue - rightValue) * direction;
  }

  return String(leftValue).localeCompare(String(rightValue), navigator.languages, {
    sensitivity: "base",
    numeric: true
  }) * direction;
}

function sortAlarmRows(
  rows: EventLogItem[],
  column: string | null,
  direction: "asc" | "desc",
  sourceNames: Record<number, string>
): EventLogItem[] {
  if (!column) {
    return rows;
  }

  const sign = direction === "asc" ? 1 : -1;
  return rows.slice().sort((left, right) => compareAlarmRows(column, left, right, sign, sourceNames));
}

function alarmRowMatches(row: EventLogItem, filter: TextFilter): boolean {
  return anyTextMatches(filter, [
    row.message,
    row.alarmClassSymbol,
    row.alarmClassName,
    formatClassName(row.alarmClassName),
    row.alarmState,
    formatAlarmState(row.alarmState, limitStateFromProps(row.props))
  ]);
}

function onResetColumns() {
  sortColumn.value = null;
  sortDir.value = "asc";
  restoreSelection(selectedCorrelationId.value);
}

function onSortColumn(columnId: string) {
  if (sortColumn.value !== columnId) {
    sortColumn.value = columnId;
    sortDir.value = "asc";
  } else if (sortDir.value === "asc") {
    sortDir.value = "desc";
  } else {
    sortColumn.value = null;
    sortDir.value = "asc";
  }

  restoreSelection(selectedCorrelationId.value);
}

const visibleItems = computed(() => {
  const filter = parseTextFilter(searchText.value);
  const classes = selectedClassLabels.value;
  const filtered = filter.kind === "none" && classes.length === 0
    ? items.value
    : items.value.filter((row) => {
      if (filter.kind !== "none" && !alarmRowMatches(row, filter)) {
        return false;
      }

      if (classes.length === 0) {
        return true;
      }

      return classes.includes(alarmClassLabel(row.alarmClassSymbol, row.alarmClassName));
    });
  return sortAlarmRows(filtered, sortColumn.value, sortDir.value, sourceNameById.value);
});

const emptyText = computed(() => {
  if (busy.value) {
    return "Loading alarms…";
  }

  const filter = parseTextFilter(searchText.value);
  if ((filter.kind !== "none" || selectedClassLabels.value.length > 0) && items.value.length > 0) {
    return "No alarms match the filter.";
  }

  return "No active alarms.";
});

const selectedItem = computed(() => {
  if (selectedIndex.value < 0) {
    return null;
  }
  return visibleItems.value[selectedIndex.value] ?? null;
});

const sourceNameById = computed(() => {
  const map: Record<number, string> = {};
  for (const source of sources.value) {
    map[source.id] = source.name;
  }
  return map;
});

const offlineSourceIds = computed(() => {
  const ids = new Set<number>();
  for (const source of sources.value) {
    if (isDataSourceOffline(source.state)) {
      ids.add(source.id);
    }
  }
  return ids;
});

const selectedSourceOffline = computed(() => {
  const item = selectedItem.value;
  return item != null && offlineSourceIds.value.has(item.sourceId);
});

const alarmSources = computed(() =>
  sources.value.filter(
    (source) =>
      source.type === "System" ||
      (source.type === "OpcUa" && source.opcUaAlarmsEnabled) ||
      (source.type === "S7CommPlus" && source.s7AlarmsEnabled)
  )
);

const sourceFilterLabel = computed(() => {
  const selected = alarmSources.value.filter((source) => selectedSourceIds.value.includes(source.id));
  if (selected.length === 0) {
    return "All";
  }

  if (selected.length === 1) {
    return selected[0].name;
  }

  return `${selected.length} sources`;
});

const alarmClassOptions = computed(() => {
  const labels = new Set<string>();
  for (const item of alarmClasses.value) {
    const label = alarmClassLabel(item.symbol, item.name);
    if (label) {
      labels.add(label);
    }
  }

  return [...labels].sort((left, right) =>
    left.localeCompare(right, navigator.languages, { sensitivity: "base" })
  );
});

const classFilterLabel = computed(() => {
  const selected = selectedClassLabels.value;
  if (selected.length === 0) {
    return "All";
  }

  if (selected.length === 1) {
    return selected[0];
  }

  return `${selected.length} classes`;
});

type FilterChip = { key: string; kind: string; label: string; remove?: () => void };

const filterChips = computed<FilterChip[]>(() => {
  const chips: FilterChip[] = [];
  for (const id of selectedSourceIds.value) {
    const name = sources.value.find((source) => source.id === id)?.name ?? `#${id}`;
    chips.push({ key: `source-${id}`, kind: "Source", label: name, remove: () => toggleSource(id) });
  }

  for (const label of selectedClassLabels.value) {
    chips.push({ key: `class-${label}`, kind: "Class", label, remove: () => toggleClass(label) });
  }

  if (searchText.value.trim()) {
    chips.push({
      key: "text",
      kind: "Text",
      label: searchText.value.trim(),
      remove: () => {
        searchText.value = "";
      }
    });
  }

  return chips;
});

onMounted(async () => {
  window.addEventListener("keydown", onKeyDown);
  window.addEventListener("pointerdown", onPointerDown);
  readQuery();
  await loadSources();
  void refreshAlarmClasses();
  startSourceStatePoll();
  startActivePoll();
  await reloadActive(false);
});

onUnmounted(() => {
  window.removeEventListener("keydown", onKeyDown);
  window.removeEventListener("pointerdown", onPointerDown);
  poll.abort();
  stopActivePoll();
  stopSourceStatePoll();
  clearStaleTimer();
});

watch(
  () => route.fullPath,
  (fullPath, previous) => {
    if (!route.path.startsWith(PENDING_PATH)) {
      // On the way to another page
      return;
    }

    if (fullPath === syncedRoutePath) {
      // syncQuery wrote the filters that are already loaded; reading them back would trim the search text while typing.
      syncedRoutePath = "";
      return;
    }

    if (!previous) {
      return;
    }

    readQuery();
    void reloadActive(false);
  }
);

watch(searchText, () => {
  writeStoredFilters();
  rememberOffset();
  restoreSelection(selectedCorrelationId.value);
  restoreRemembered();
});

watch(alarmClassOptions, (options) => {
  if (alarmClasses.value.length === 0) {
    return;
  }

  const allowed = new Set(options);
  const kept = selectedClassLabels.value.filter((label) => allowed.has(label));
  if (kept.length !== selectedClassLabels.value.length) {
    selectedClassLabels.value = kept;
    writeStoredFilters();
  }
});

function toggleFilters() {
  filtersCollapsed.value = !filtersCollapsed.value;
  if (filtersCollapsed.value) {
    sourceMenuOpen.value = false;
    classMenuOpen.value = false;
  }
}

function closeFilters() {
  filtersCollapsed.value = true;
  sourceMenuOpen.value = false;
  classMenuOpen.value = false;
}

function toggleSourceMenu() {
  sourceMenuOpen.value = !sourceMenuOpen.value;
  if (sourceMenuOpen.value) {
    classMenuOpen.value = false;
  }
}

// The class filter works on the list that is here: the list is filtered again and the selected row looked for
function classFilterChanged() {
  writeStoredFilters();
  rememberOffset();
  restoreSelection(selectedCorrelationId.value);
  restoreRemembered();
}

// The context menu of a row: the filter then holds only that source or class
function filterBySource(id: number) {
  if (selectedSourceIds.value.length === 1 && selectedSourceIds.value[0] === id) {
    return;
  }

  selectedSourceIds.value = [id];
  void reloadActive(false);
}

function filterByClass(label: string) {
  const current = selectedClassLabels.value;
  if (current.length === 1 && current[0] === label) {
    return;
  }

  selectedClassLabels.value = [label];
  classFilterChanged();
}

function selectAllSources() {
  if (selectedSourceIds.value.length === 0) {
    return;
  }

  selectedSourceIds.value = [];
  void reloadActive(false);
}

function toggleSource(id: number) {
  const current = selectedSourceIds.value;
  selectedSourceIds.value = current.includes(id)
    ? current.filter((item) => item !== id)
    : [...current, id];
  void reloadActive(false);
}

function toggleClassMenu() {
  classMenuOpen.value = !classMenuOpen.value;
  if (classMenuOpen.value) {
    sourceMenuOpen.value = false;
  }
}

function selectAllClasses() {
  if (selectedClassLabels.value.length === 0) {
    return;
  }

  selectedClassLabels.value = [];
  classFilterChanged();
}

function toggleClass(label: string) {
  const current = selectedClassLabels.value;
  selectedClassLabels.value = current.includes(label)
    ? current.filter((item) => item !== label)
    : [...current, label];
  classFilterChanged();
}

function onPointerDown(event: PointerEvent) {
  closeMenuIfOutside(sourceMenuOpen, sourceMenuRef, event);
  closeMenuIfOutside(classMenuOpen, classMenuRef, event);
}

function closeMenuIfOutside(
  open: { value: boolean },
  root: { value: HTMLElement | null },
  event: PointerEvent
) {
  if (!open.value) {
    return;
  }

  if (root.value && event.target instanceof Node && root.value.contains(event.target)) {
    return;
  }

  open.value = false;
}

type StoredAlarmFilters = {
  sourceIds: number[];
  classLabels: string[];
  q: string;
};

function readStoredFilters(): StoredAlarmFilters | null {
  try {
    let raw = window.localStorage.getItem(ALARM_FILTERS_KEY);
    if (!raw) {
      // The filters of the time before every page had its own: only those that were set on this page
      raw = window.localStorage.getItem(LEGACY_ALARM_FILTERS_KEY);
      if (raw && (JSON.parse(raw) as { mode?: string }).mode === "archive") {
        raw = null;
      }
    }
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as Partial<StoredAlarmFilters>;
    const sourceIds = Array.isArray(parsed.sourceIds)
      ? parsed.sourceIds.filter((id) => Number.isInteger(id) && id > 0)
      : [];
    const classLabels = Array.isArray(parsed.classLabels)
      ? parsed.classLabels.filter((label) => typeof label === "string" && label.trim().length > 0).map((label) => label.trim())
      : [];
    return {
      sourceIds,
      classLabels,
      q: typeof parsed.q === "string" ? parsed.q : ""
    };
  } catch {
    return null;
  }
}

function writeStoredFilters() {
  const stored: StoredAlarmFilters = {
    sourceIds: selectedSourceIds.value,
    classLabels: selectedClassLabels.value,
    q: searchText.value.trim()
  };
  try {
    window.localStorage.setItem(ALARM_FILTERS_KEY, JSON.stringify(stored));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

function queryHasFilters(): boolean {
  const query = route.query;
  return typeof query.sourceIds === "string"
    || typeof query.sourceId === "string"
    || typeof query.q === "string";
}

function readQuery() {
  const stored = readStoredFilters();
  if (!queryHasFilters()) {
    if (stored) {
      selectedSourceIds.value = stored.sourceIds;
      selectedClassLabels.value = stored.classLabels;
      searchText.value = stored.q;
    }
    return;
  }

  if (typeof route.query.q === "string") {
    searchText.value = route.query.q;
  } else if (stored) {
    searchText.value = stored.q;
  }

  if (typeof route.query.sourceIds === "string" || typeof route.query.sourceId === "string") {
    const rawSourceIds = typeof route.query.sourceIds === "string"
      ? route.query.sourceIds
      : typeof route.query.sourceId === "string"
        ? route.query.sourceId
        : "";
    selectedSourceIds.value = parseSourceIds(rawSourceIds);
  } else if (stored) {
    selectedSourceIds.value = stored.sourceIds;
  }

  if (stored) {
    selectedClassLabels.value = stored.classLabels;
  }
}

function syncQuery() {
  writeStoredFilters();
  const next = {
    q: searchText.value.trim(),
    sourceIds: selectedSourceIds.value.length > 0 ? selectedSourceIds.value.join(",") : ""
  };
  const current = route.query;
  if (
    queryText(current.q) === next.q
    && queryText(current.sourceIds) === next.sourceIds
    // Left over from a link to another page or from the time before the pages were apart
    && queryText(current.mode) === ""
    && queryText(current.sourceId) === ""
    && queryText(current.range) === ""
    && queryText(current.from) === ""
    && queryText(current.to) === ""
    && queryText(current.state) === ""
  ) {
    return;
  }

  const target = {
    query: {
      q: next.q || undefined,
      sourceIds: next.sourceIds || undefined
    }
  };
  syncedRoutePath = router.resolve(target).fullPath;
  void router.replace(target);
}

function activeSourceIds(): string | undefined {
  return selectedSourceIds.value.length > 0 ? selectedSourceIds.value.join(",") : undefined;
}

async function loadSources() {
  try {
    sources.value = await getDataSources();
  } catch {
    sources.value = [];
    return;
  }

  const allowed = new Set(alarmSources.value.map((source) => source.id));
  const kept = selectedSourceIds.value.filter((id) => allowed.has(id));
  if (kept.length !== selectedSourceIds.value.length) {
    selectedSourceIds.value = kept;
  }
}

function markUpdateFresh() {
  updatesStale.value = false;
  clearStaleTimer();
  staleTimer = window.setTimeout(() => {
    updatesStale.value = true;
  }, UPDATE_STALE_MS);
}

function clearStaleTimer() {
  if (staleTimer !== undefined) {
    window.clearTimeout(staleTimer);
    staleTimer = undefined;
  }
}

function startSourceStatePoll() {
  stopSourceStatePoll();
  sourceStateTimer = window.setInterval(() => {
    if (document.visibilityState !== "visible") {
      return;
    }

    void refreshSourceStates();
    void refreshAlarmClasses();
  }, SOURCE_STATE_POLL_MS);
}

function stopSourceStatePoll() {
  if (sourceStateTimer !== undefined) {
    window.clearInterval(sourceStateTimer);
    sourceStateTimer = undefined;
  }

  sourceStateAbort?.abort();
  sourceStateAbort = undefined;
}

async function refreshSourceStates() {
  sourceStateAbort?.abort();
  const controller = new AbortController();
  sourceStateAbort = controller;

  try {
    const next = await getDataSources(controller.signal);
    if (sourceStateAbort !== controller) {
      return;
    }

    sources.value = next;
  } catch {
    // Keep the last known connection state.
  }
}

async function refreshAlarmClasses() {
  try {
    alarmClasses.value = await getAlarmClasses();
  } catch {
    // Keep the last known class list.
  }
}

async function reloadActive(silent: boolean) {
  if (!silent) {
    rememberOffset();
    syncQuery();
    busy.value = true;
    error.value = "";
    selectedIndex.value = -1;
    selectedCorrelationId.value = "";
  }

  const session = poll.begin();
  const keepId = silent ? selectedCorrelationId.value : "";

  const started = performance.now();
  try {
    const page = await getActiveAlarms({ sourceIds: activeSourceIds() }, session.signal);
    const elapsedMs = Math.max(0, Math.round(performance.now() - started));
    if (!session.isCurrent()) {
      return;
    }

    updateMs.value = elapsedMs;
    updateSeq.value += 1;
    markUpdateFresh();

    items.value = page.items;
    summary.value = {
      from: page.from,
      to: page.to,
      total: page.items.length,
      activeAlarms: page.items.length,
      countByLevel: {}
    };
    if (silent) {
      restoreSelection(keepId);
    } else {
      restoreRemembered();
    }
  } catch (err) {
    if (!session.isCurrent() || isCancelledError(err)) {
      return;
    }

    if (!silent) {
      error.value = err instanceof Error ? err.message : "Failed to load alarms.";
    }
  } finally {
    if (session.isCurrent()) {
      busy.value = false;
    }
  }
}

function restoreSelection(correlationId: string) {
  if (!correlationId) {
    return;
  }

  const index = visibleItems.value.findIndex((row) => row.correlationId === correlationId);
  selectedIndex.value = index;
  if (index < 0) {
    selectedCorrelationId.value = "";
  }
}

/**
 * The row that was selected last. It is remembered when the filter changes: it is selected again, with its details, as
 * soon as a filter shows it, and while it is not in the list the list lies at its time. Another selection replaces it,
 * closing the details forgets it.
 */
type RememberedRow = {
  correlationId: string;
  time: number;
  /** Where the row stood in the table when the list last changed, in rows below its upper edge. */
  offset: number | null;
};
let remembered: RememberedRow | null = null;

function remember(row: EventLogItem | undefined) {
  remembered = row?.correlationId
    ? { correlationId: row.correlationId, time: Date.parse(row.time), offset: null }
    : null;
}

// Called before a filter changes the list: the selected row is still where the user left it. The row comes back to the
// same place in the table, also after several filters in which it was not in the list.
function rememberOffset() {
  if (remembered && selectedIndex.value >= 0) {
    remembered.offset = eventTableRef.value?.rowOffsetInView(selectedIndex.value) ?? remembered.offset;
  }
}

// The whole list is here, so the row is found by its alarm; else the list scrolls to its time
function restoreRemembered() {
  if (!remembered || selectedIndex.value >= 0) {
    return;
  }
  const rows = visibleItems.value;
  if (rows.length === 0) {
    return;
  }
  const place = remembered.offset ?? "center";
  const index = rows.findIndex((row) => row.correlationId === remembered!.correlationId);
  if (index >= 0) {
    onSelect(index);
    eventTableRef.value?.scrollToIndex(index, place);
    return;
  }
  const near = nearestRowByTime(rows, remembered.time);
  if (near >= 0) {
    eventTableRef.value?.scrollToIndex(near, place);
  }
}

// The index of the row whose time is the first at or after `time` in the order of the list (newest first or oldest first,
// as the list is), the last one when none is; -1 when the list is not ordered by time
function nearestRowByTime(rows: EventLogItem[], time: number): number {
  if (sortColumn.value !== null && sortColumn.value !== "time") {
    return -1;
  }
  const times = rows.map((row) => Date.parse(row.time));
  const newestFirst = times[0] >= times[times.length - 1];
  const index = times.findIndex((value) => (newestFirst ? value <= time : value >= time));
  return index >= 0 ? index : times.length - 1;
}

function ignoresArrowSelection(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }
  if (target.closest("button, textarea, select, a, .events-source")) {
    return true;
  }
  if (target instanceof HTMLInputElement && target.type !== "checkbox") {
    return true;
  }
  return target.isContentEditable;
}

function closeDetails() {
  selectedIndex.value = -1;
  selectedCorrelationId.value = "";
  // Closing the details is a decision: the row is not selected again when a filter shows it later
  remembered = null;
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === "Escape") {
    if (sourceMenuOpen.value) {
      event.preventDefault();
      sourceMenuOpen.value = false;
      return;
    }
    if (selectedIndex.value < 0) {
      return;
    }
    event.preventDefault();
    closeDetails();
    return;
  }
  if (event.key !== "ArrowUp" && event.key !== "ArrowDown") {
    return;
  }
  if (event.altKey || event.ctrlKey || event.metaKey || ignoresArrowSelection(event.target)) {
    return;
  }
  const rowCount = visibleItems.value.length;
  if (selectedIndex.value < 0 || rowCount === 0) {
    return;
  }
  event.preventDefault();
  const delta = event.key === "ArrowDown" ? 1 : -1;
  const next = Math.min(rowCount - 1, Math.max(0, selectedIndex.value + delta));
  if (next !== selectedIndex.value) {
    onSelect(next);
  }
  eventTableRef.value?.scrollToIndex(next);
}

function onSelect(index: number) {
  selectedIndex.value = index;
  const row = visibleItems.value[index];
  selectedCorrelationId.value = row?.correlationId ?? "";
  // A row that is picked replaces the one that was remembered
  remember(row);
}

function startActivePoll() {
  stopActivePoll();
  activeTimer = window.setInterval(() => {
    if (document.visibilityState !== "visible") {
      return;
    }

    void reloadActive(true);
  }, ACTIVE_POLL_MS);
}

function stopActivePoll() {
  if (activeTimer !== undefined) {
    window.clearInterval(activeTimer);
    activeTimer = undefined;
  }
}
</script>
