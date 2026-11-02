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
        <h1>History</h1>
        <div
          v-if="archiveStatus === 'loading' || archiveStatus === 'ready' || archiveCountStatus === 'loading' || archiveCountStatus === 'ready' || archiveFailed"
          class="page-header-actions events-summary-badges"
        >
          <span class="badge default" :data-hint="archiveBadgeTitle">{{ archiveBadgeText }}</span>
          <span
            class="badge alarm-update"
            :class="{ 'is-loading': archiveLoadBusy, 'is-done': !archiveLoadBusy && !archiveFailed, 'is-failed': archiveFailed }"
            :style="{ '--alarm-update-ms': `${ARCHIVE_UPDATE_MS}ms` }"
            :data-hint="archiveUpdateTitle"
            :aria-label="archiveUpdateTitle"
          >
            <span :key="archiveUpdateSeq" class="alarm-update-bar" aria-hidden="true" />
            <span class="alarm-update-label">{{ archiveUpdateLabel }}</span>
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
            <button type="button" class="btn secondary btn-sm" @click="reloadHistory">Retry</button>
          </div>

          <div class="events-table-shell">
            <VirtualEventTable
              ref="eventTableRef"
              storage-key="alarmColumnsHistory"
              :items="noItems"
              :total="archiveVirtualTotal"
              :get-row="archiveRowAt"
              :revision="archivePageRevision"
              :selected-index="selectedIndex"
              :source-name-by-id="sourceNameById"
              :empty-text="emptyText"
              sortable
              :sort-column="sortColumn"
              :sort-direction="sortDir"
              :state-filter-of="stateFilterOf"
              @select="onSelect"
              @sort="onSortColumn"
              @reset="onResetColumns"
              @range="onArchiveRange"
              @filter-source="filterBySource"
              @filter-class="filterByClass"
              @filter-state="filterByState"
            />
          </div>

          <AlarmDetailPanel
            storage-key="alarmDetailHeightHistory"
            statistics
            :item="selectedItem"
            :source-label="selectedItem ? sourceLabelFor(selectedItem, sourceNameById) : ''"
            @close="closeDetails"
          >
            <template #message-extra>
              <div v-if="selectedItem?.correlationId && alarmHistory.length > 0" class="events-alarm-history">
                <h3>Message history</h3>
                <ul>
                  <li v-for="(entry, index) in alarmHistory" :key="`${index}-${entry.time}-${entry.alarmState}`">
                    <AlarmStateIcon :state="entry.alarmState" :limit="limitStateFromProps(entry.props)" />
                    <span>{{ formatDateTimeMs(new Date(entry.time)) }}</span>
                    <span>{{ formatAlarmState(entry.alarmState, limitStateFromProps(entry.props)) }}</span>
                    <span>{{ formatDurationMs(entry.durationMs) }}</span>
                  </li>
                </ul>
              </div>
            </template>
          </AlarmDetailPanel>
        </section>

        <AlarmFiltersPanel
          v-model:width="filtersWidth"
          storage-key="alarmFiltersWidthHistory"
          :collapsed="filtersCollapsed"
          @close="closeFilters"
        >
          <h2>Time range</h2>
          <div class="events-presets">
            <button
              v-for="preset in presets"
              :key="preset.id"
              type="button"
              class="btn secondary btn-sm"
              :class="{ active: startCustomMs === null && activePreset === preset.id }"
              @click="applyPreset(preset.id)"
            >
              {{ preset.label }}
            </button>
          </div>
          <details class="events-fold" :open="customRangeOpen" @toggle="onRangeToggle">
            <summary>Custom range</summary>
            <h3 class="events-sub">Start time</h3>
            <input
              type="datetime-local"
              step="1"
              class="events-select events-start-input"
              :class="{ active: startCustomMs !== null }"
              :value="toInputValue(startCustomMs ?? rangeFromMs)"
              data-hint="The range starts at this time"
              aria-label="Start time"
              @change="onStartInput"
            />
            <h3 class="events-sub">End time</h3>
            <div class="events-start">
              <button
                type="button"
                class="btn secondary btn-sm"
                :class="{ active: endCustomMs === null }"
                data-hint="The range ends now and follows the clock"
                @click="setEndNow"
              >
                Now
              </button>
              <input
                type="datetime-local"
                step="1"
                class="events-select events-start-input"
                :class="{ active: endCustomMs !== null }"
                :value="toInputValue(endCustomMs ?? rangeToMs)"
                data-hint="The range ends at this time"
                aria-label="End time"
                @change="onEndInput"
              />
            </div>
            <h3 class="events-sub">Move range</h3>
            <div class="events-shift">
              <button
                type="button"
                class="btn secondary btn-sm"
                data-hint="Move the range back in time by its length"
                aria-label="Move the range back"
                @click="shiftRange(-1)"
              >
                &lsaquo; Back
              </button>
              <button
                type="button"
                class="btn secondary btn-sm"
                data-hint="Move the range forward in time by its length"
                aria-label="Move the range forward"
                :disabled="endCustomMs === null"
                @click="shiftRange(1)"
              >
                Forward &rsaquo;
              </button>
            </div>
          </details>

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

          <h2>State</h2>
          <div class="events-source" ref="stateMenuRef">
            <button
              type="button"
              class="events-select events-source-toggle"
              :aria-expanded="stateMenuOpen"
              aria-haspopup="listbox"
              @click="toggleStateMenu"
            >
              <span>{{ stateFilterLabel }}</span>
            </button>
            <div v-show="stateMenuOpen" class="events-source-menu" role="listbox" aria-label="States" aria-multiselectable="true">
              <label class="events-source-option">
                <input
                  type="checkbox"
                  :checked="selectedStates.length === 0"
                  @change="selectAllStates"
                />
                <span>All</span>
              </label>
              <label v-for="state in stateOptions" :key="state.id" class="events-source-option">
                <input
                  type="checkbox"
                  :checked="selectedStates.includes(state.id)"
                  @change="toggleState(state.id)"
                />
                <span>{{ state.label }}</span>
              </label>
              <p v-if="stateOptions.length === 0" class="events-source-empty">No alarm states in this range.</p>
            </div>
          </div>
        </AlarmFiltersPanel>
      </div>
    </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import AlarmDetailPanel from "../components/AlarmDetailPanel.vue";
import AlarmFiltersPanel from "../components/AlarmFiltersPanel.vue";
import AlarmStateIcon from "../components/AlarmStateIcon.vue";
import VirtualEventTable from "../components/VirtualEventTable.vue";
import {
  createPollSession,
  getAlarmClasses,
  getAlarmHistory,
  getAlarmStates,
  getDataSources,
  getEventLogCount,
  getEventLogPage,
  isCancelledError
} from "../api";
import { parseSourceIds, queryText, readFiltersWidth, sourceLabelFor, storedTime } from "../alarmCommon";
import { alarmClassLabel, alarmStateBase, formatAlarmState, formatDateTime, formatDateTimeMs, formatDurationMs, formatInteger, limitStateFromProps } from "../format";
import { parseTextFilter } from "../textFilter";
import type { AlarmClassItem, DataSourceListItem, EventLogItem } from "../types";

const ARCHIVE_PAGE_SIZE = 50;
const ARCHIVE_UPDATE_MS = 1000;
const SOURCE_STATE_POLL_MS = 5000;
const HISTORY_PATH = "/alarms/history";
const PRESET_MATCH_MS = 60_000;
const SEARCH_DEBOUNCE_MS = 300;
// The filters, the width of the filters and the columns of this page are kept in the browser apart from the pending alarms
const ALARM_FILTERS_KEY = "alarmFiltersHistory";
const FILTERS_WIDTH_KEY = "alarmFiltersWidthHistory";
// The keys of the time before every page had its own
const LEGACY_ALARM_FILTERS_KEY = "alarmFilters";
const LEGACY_FILTERS_WIDTH_KEY = "alarmFiltersWidth";
/** Columns the archive can sort on. Mirrors EventSortColumn of the API. */
const ARCHIVE_SORT_COLUMNS = new Set([
  "time",
  "state",
  "severity",
  "active",
  "acked",
  "retain",
  "duration",
  "source",
  "class",
  "type",
  "message"
]);

const presets = [
  { id: "15m", label: "Last 15m", ms: 15 * 60 * 1000 },
  { id: "1h", label: "Last 1h", ms: 60 * 60 * 1000 },
  { id: "24h", label: "Last 24h", ms: 24 * 60 * 60 * 1000 },
  { id: "7d", label: "Last 7d", ms: 7 * 24 * 60 * 60 * 1000 }
];

const allStateOptions = [
  { id: "Incoming", label: "Incoming" },
  { id: "Incoming/Outgoing", label: "Incoming/Outgoing" },
  { id: "Removed", label: "Removed" },
  { id: "Normal", label: "Normal" },
  { id: "Disabled", label: "Disabled" },
  { id: "OutOfService", label: "Out of service" },
  { id: "Suppressed", label: "Suppressed" },
  { id: "Shelved", label: "Shelved" },
  { id: "SuppressedOrShelved", label: "Suppressed or shelved" }
];

// States that occur in the loaded history range. Null until the first answer.
const presentStates = ref<string[] | null>(null);

const stateOptions = computed(() =>
  presentStates.value === null
    ? allStateOptions
    : allStateOptions.filter(
        (state) => presentStates.value!.includes(state.id) || selectedStates.value.includes(state.id)
      )
);

// The rows come from the pages of the archive, so the table gets none of its own
const noItems: EventLogItem[] = [];
const eventTableRef = ref<{
  scrollToIndex: (index: number, place?: number | "center") => void;
  rowOffsetInView: (index: number) => number | null;
} | null>(null);
const route = useRoute();
const router = useRouter();
const poll = createPollSession();

const sources = ref<DataSourceListItem[]>([]);
const busy = ref(false);
const archiveStatus = ref<"idle" | "loading" | "ready" | "failed">("idle");
const archiveCountStatus = ref<"idle" | "loading" | "ready" | "failed">("idle");
const archiveTotal = ref<number | null>(null);
// Alarms of the range while a sort shows only the first ones of its order (archiveTotal of them); else null.
const archiveSortedOf = ref<number | null>(null);
// Alarms counted so far while the archive count is still running on a slow machine.
const archiveCountProgress = ref<number | null>(null);
const archivePages = ref(new Map<number, EventLogItem[]>());
const archivePageRevision = ref(0);
const archiveVisibleStart = ref(0);
const archiveVisibleEnd = ref(-1);
const archivePageBusy = ref(false);
const archiveUpdateSeq = ref(0);
let archivePageGeneration = 0;
let archivePageAbort: AbortController | null = null;
// Page index of the request in flight. A scroll keeps it running while that page is still needed.
let archivePageLoading: number | null = null;
const error = ref("");
const selectedIndex = ref(-1);
const selectedCorrelationId = ref("");
const activePreset = ref("24h");
const searchText = ref("");
const selectedStates = ref<string[]>([]);
const selectedSourceIds = ref<number[]>([]);
const selectedClassLabels = ref<string[]>([]);
const alarmClasses = ref<AlarmClassItem[]>([]);
const alarmHistory = ref<EventLogItem[]>([]);
const sourceMenuOpen = ref(false);
const classMenuOpen = ref(false);
const stateMenuOpen = ref(false);
const sourceMenuRef = ref<HTMLElement | null>(null);
const classMenuRef = ref<HTMLElement | null>(null);
const stateMenuRef = ref<HTMLElement | null>(null);
const filtersCollapsed = ref(true);
const filtersWidth = ref(readFiltersWidth(FILTERS_WIDTH_KEY, LEGACY_FILTERS_WIDTH_KEY));
const sortColumn = ref<string | null>(null);
const sortDir = ref<"asc" | "desc">("asc");

// Chosen start and end of the range. No start: one time range before the end; no end: Now.
const startCustomMs = ref<number | null>(null);
const endCustomMs = ref<number | null>(null);
const rangeToMs = ref(Date.now());
const rangeFromMs = ref(Date.now() - 24 * 60 * 60 * 1000);

let sourceStateTimer: number | undefined;
let searchTimer: number | undefined;
// Search text of the last history load and the route syncQuery wrote last.
let loadedSearch = "";
let syncedRoutePath = "";
let sourceStateAbort: AbortController | undefined;
let alarmHistoryAbort: AbortController | undefined;
// The state list reads the whole range. A newer range or leaving the page stops the older read.
let presentStatesAbort: AbortController | undefined;

function onResetColumns() {
  if (sortColumn.value === null) {
    return;
  }

  sortColumn.value = null;
  sortDir.value = "asc";
  reloadArchiveSorted();
}

function onSortColumn(columnId: string) {
  if (!ARCHIVE_SORT_COLUMNS.has(columnId)) {
    // The archive sorts on storage fields, so a column without one keeps its order.
    return;
  }

  if (sortColumn.value !== columnId) {
    sortColumn.value = columnId;
    sortDir.value = "asc";
  } else if (sortDir.value === "asc") {
    sortDir.value = "desc";
  } else {
    sortColumn.value = null;
    sortDir.value = "asc";
  }

  reloadArchiveSorted();
}

/**
 * The archive sorts on the server, so the loaded pages are dropped and the first page of the new
 * order is read again. The alarm count does not depend on the order and stays as it is.
 */
function reloadArchiveSorted() {
  // The count of the range does not depend on the order; a shortened sort gives it back.
  if (archiveSortedOf.value !== null) {
    archiveTotal.value = archiveSortedOf.value;
    archiveSortedOf.value = null;
  }
  selectedIndex.value = -1;
  selectedCorrelationId.value = "";
  cancelAlarmHistory();
  cancelArchivePages();
  clearArchivePages();
  archiveVisibleStart.value = 0;
  archiveVisibleEnd.value = -1;
  error.value = "";
  eventTableRef.value?.scrollToIndex(0);
  void loadArchivePage(0);
}

function archiveSortParams(): { sort1?: string; dir1?: "asc" | "desc" } {
  if (!sortColumn.value) {
    return {};
  }

  return { sort1: sortColumn.value, dir1: sortDir.value };
}

const emptyText = computed(() => {
  if (archiveStatus.value === "failed" || archiveCountStatus.value === "failed") {
    return "Could not load alarms.";
  }

  if (archiveCountStatus.value !== "ready") {
    return archiveCountProgress.value != null && archiveCountProgress.value > 0
      ? `Counting alarms… ${formatInteger(archiveCountProgress.value)} so far`
      : "Loading alarms…";
  }

  return parseTextFilter(searchText.value).kind === "none"
    ? "No alarms in this time range."
    : "No alarms match the filter.";
});

function alarmCountLabel(count: number): string {
  return `${formatInteger(count)} ${count === 1 ? "alarm" : "alarms"}`;
}

const archiveBadgeText = computed(() => {
  if (archiveCountStatus.value === "ready" && archiveTotal.value !== null && archiveSortedOf.value !== null) {
    return `${formatInteger(archiveTotal.value)} of ${formatInteger(archiveSortedOf.value)} alarms sorted`;
  }

  if (archiveCountStatus.value === "ready" && archiveTotal.value !== null) {
    return alarmCountLabel(archiveTotal.value);
  }

  if (archiveStatus.value === "failed" || archiveCountStatus.value === "failed") {
    return "Failed";
  }

  if (archiveCountProgress.value != null && archiveCountProgress.value > 0) {
    return `${formatInteger(archiveCountProgress.value)}…`;
  }

  return "…";
});

const archiveBadgeTitle = computed(() =>
  archiveSortedOf.value !== null && archiveTotal.value !== null
    ? `The time range has ${formatInteger(archiveSortedOf.value)} alarms. Only the first ${formatInteger(archiveTotal.value)} `
      + "of this order are sorted. Reverse the order or shorten the time range to see the others."
    : undefined
);

const archiveFailed = computed(
  () => archiveStatus.value === "failed" || archiveCountStatus.value === "failed"
);

const archiveLoadBusy = computed(
  () => !archiveFailed.value && (archiveCountStatus.value === "loading" || archivePageBusy.value)
);

const archiveUpdateLabel = computed(() => {
  if (archiveFailed.value) {
    return "Failed";
  }

  return archiveLoadBusy.value ? "Loading" : "OK";
});

const archiveUpdateTitle = computed(() => {
  if (archiveFailed.value) {
    return "Loading alarms from the archive failed. Use Retry to load the time range again.";
  }

  return archiveLoadBusy.value ? "Loading alarms from the archive." : "Archive load finished.";
});

const archiveVirtualTotal = computed(() => {
  if (archiveCountStatus.value !== "ready" || archiveTotal.value === null) {
    return null;
  }
  return archiveTotal.value;
});

const selectedItem = computed(() => {
  if (selectedIndex.value < 0) {
    return null;
  }
  return archiveRowAt(selectedIndex.value) ?? null;
});

const sourceNameById = computed(() => {
  const map: Record<number, string> = {};
  for (const source of sources.value) {
    map[source.id] = source.name;
  }
  return map;
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

const stateFilterLabel = computed(() => {
  const selected = selectedStates.value;
  if (selected.length === 0) {
    return "All";
  }

  if (selected.length === 1) {
    return stateLabel(selected[0]);
  }

  return `${selected.length} states`;
});

function stateLabel(id: string): string {
  return allStateOptions.find((entry) => entry.id === id)?.label ?? id;
}

type FilterChip = { key: string; kind: string; label: string; remove?: () => void };

const filterChips = computed<FilterChip[]>(() => {
  const chips: FilterChip[] = [];
  if (startCustomMs.value === null) {
    // A time range that ends now.
    const preset = presets.find((entry) => entry.id === activePreset.value);
    if (preset) {
      chips.push({ key: "range", kind: "Time", label: preset.label });
    }
  } else {
    // A chosen window: both ends are shown. The start clears the whole window (back to the time range),
    // the end falls back to Now.
    chips.push({
      key: "start",
      kind: "Start",
      label: formatDateTime(new Date(startCustomMs.value)),
      remove: resetRange
    });
    chips.push({
      key: "end",
      kind: "End",
      label: endCustomMs.value === null ? "Now" : formatDateTime(new Date(endCustomMs.value)),
      remove: endCustomMs.value === null ? undefined : setEndNow
    });
  }

  for (const state of selectedStates.value) {
    chips.push({ key: `state-${state}`, kind: "State", label: stateLabel(state), remove: () => toggleState(state) });
  }

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
  await reloadHistory();
});

onUnmounted(() => {
  window.removeEventListener("keydown", onKeyDown);
  window.removeEventListener("pointerdown", onPointerDown);
  poll.abort();
  presentStatesAbort?.abort();
  cancelAlarmHistory();
  cancelArchivePages();
  stopSourceStatePoll();
  clearSearchTimer();
});

watch(
  () => route.fullPath,
  (fullPath, previous) => {
    if (!route.path.startsWith(HISTORY_PATH)) {
      // On the way to another page
      return;
    }

    if (fullPath === syncedRoutePath) {
      // syncQuery wrote the filters that are already loaded; reading them back would
      // trim the search text while typing and load the page twice.
      syncedRoutePath = "";
      return;
    }

    if (!previous) {
      return;
    }

    readQuery();
    void reloadHistory();
  }
);

watch(archivePageRevision, () => {
  tryRestoreArchive();
  if (selectedIndex.value < 0) {
    return;
  }

  const row = archiveRowAt(selectedIndex.value);
  if (!row?.correlationId) {
    return;
  }

  if (row.correlationId === selectedCorrelationId.value && alarmHistory.value.length > 0) {
    return;
  }

  selectedCorrelationId.value = row.correlationId;
  void loadAlarmHistory();
});

watch(archiveCountStatus, (status) => {
  if (status === "ready") {
    tryRestoreArchive();
  }
});

watch(searchText, (text) => {
  writeStoredFilters();
  clearSearchTimer();
  if (text.trim() === loadedSearch) {
    return;
  }

  searchTimer = window.setTimeout(() => {
    searchTimer = undefined;
    void reloadHistory();
  }, SEARCH_DEBOUNCE_MS);
});

function clearSearchTimer() {
  if (searchTimer !== undefined) {
    window.clearTimeout(searchTimer);
    searchTimer = undefined;
  }
}

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
    stateMenuOpen.value = false;
  }

  customRangeOpen.value = false;
}

function closeFilters() {
  filtersCollapsed.value = true;
  sourceMenuOpen.value = false;
  classMenuOpen.value = false;
  stateMenuOpen.value = false;
  customRangeOpen.value = false;
}

function toggleSourceMenu() {
  sourceMenuOpen.value = !sourceMenuOpen.value;
  if (sourceMenuOpen.value) {
    classMenuOpen.value = false;
    stateMenuOpen.value = false;
  }
}

// The context menu of a row: the filter then holds only that source, class or state
function filterBySource(id: number) {
  if (selectedSourceIds.value.length === 1 && selectedSourceIds.value[0] === id) {
    return;
  }

  selectedSourceIds.value = [id];
  void reloadHistory();
}

function filterByClass(label: string) {
  const current = selectedClassLabels.value;
  if (current.length === 1 && current[0] === label) {
    return;
  }

  selectedClassLabels.value = [label];
  void reloadHistory();
}

function stateFilterOf(row: EventLogItem): string {
  return knownState(alarmStateBase(row.alarmState));
}

function filterByState(state: string) {
  if (selectedStates.value.length === 1 && selectedStates.value[0] === state) {
    return;
  }

  selectedStates.value = [state];
  void reloadHistory();
}

function selectAllSources() {
  if (selectedSourceIds.value.length === 0) {
    return;
  }

  selectedSourceIds.value = [];
  void reloadHistory();
}

function toggleSource(id: number) {
  const current = selectedSourceIds.value;
  selectedSourceIds.value = current.includes(id)
    ? current.filter((item) => item !== id)
    : [...current, id];
  void reloadHistory();
}

function toggleClassMenu() {
  classMenuOpen.value = !classMenuOpen.value;
  if (classMenuOpen.value) {
    sourceMenuOpen.value = false;
    stateMenuOpen.value = false;
  }
}

function selectAllClasses() {
  if (selectedClassLabels.value.length === 0) {
    return;
  }

  selectedClassLabels.value = [];
  void reloadHistory();
}

function toggleClass(label: string) {
  const current = selectedClassLabels.value;
  selectedClassLabels.value = current.includes(label)
    ? current.filter((item) => item !== label)
    : [...current, label];
  void reloadHistory();
}

function toggleStateMenu() {
  stateMenuOpen.value = !stateMenuOpen.value;
  if (stateMenuOpen.value) {
    sourceMenuOpen.value = false;
    classMenuOpen.value = false;
  }
}

function selectAllStates() {
  if (selectedStates.value.length === 0) {
    return;
  }

  selectedStates.value = [];
  void reloadHistory();
}

function toggleState(state: string) {
  const current = selectedStates.value;
  selectedStates.value = current.includes(state)
    ? current.filter((item) => item !== state)
    : [...current, state];
  void reloadHistory();
}

function onPointerDown(event: PointerEvent) {
  closeMenuIfOutside(sourceMenuOpen, sourceMenuRef, event);
  closeMenuIfOutside(classMenuOpen, classMenuRef, event);
  closeMenuIfOutside(stateMenuOpen, stateMenuRef, event);
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
  preset: string;
  states: string[];
  sourceIds: number[];
  classLabels: string[];
  q: string;
  startMs: number | null;
  endMs: number | null;
};

function knownState(value: string): string {
  return allStateOptions.some((state) => state.id === value) ? value : "";
}

// The known states of a comma-separated list (the URL) or an array (stored), each once.
function knownStates(value: unknown): string[] {
  const parts = typeof value === "string" ? value.split(",") : Array.isArray(value) ? value : [];
  const states: string[] = [];
  for (const part of parts) {
    const state = typeof part === "string" ? knownState(part.trim()) : "";
    if (state && !states.includes(state)) {
      states.push(state);
    }
  }

  return states;
}

function readStoredFilters(): StoredAlarmFilters | null {
  try {
    let raw = window.localStorage.getItem(ALARM_FILTERS_KEY);
    if (!raw) {
      // The filters of the time before every page had its own: only those that were set on this page
      raw = window.localStorage.getItem(LEGACY_ALARM_FILTERS_KEY);
      if (raw && (JSON.parse(raw) as { mode?: string }).mode !== "archive") {
        raw = null;
      }
    }
    if (!raw) {
      return null;
    }

    // Before several states could be chosen there was one `state`.
    const parsed = JSON.parse(raw) as Partial<StoredAlarmFilters> & { state?: unknown };
    const sourceIds = Array.isArray(parsed.sourceIds)
      ? parsed.sourceIds.filter((id) => Number.isInteger(id) && id > 0)
      : [];
    const classLabels = Array.isArray(parsed.classLabels)
      ? parsed.classLabels.filter((label) => typeof label === "string" && label.trim().length > 0).map((label) => label.trim())
      : [];
    return {
      preset: typeof parsed.preset === "string" ? parsed.preset : "",
      states: knownStates(parsed.states ?? parsed.state),
      sourceIds,
      classLabels,
      q: typeof parsed.q === "string" ? parsed.q : "",
      startMs: storedTime(parsed.startMs),
      endMs: storedTime(parsed.endMs)
    };
  } catch {
    return null;
  }
}

function writeStoredFilters() {
  const stored: StoredAlarmFilters = {
    preset: activePreset.value,
    states: selectedStates.value,
    sourceIds: selectedSourceIds.value,
    classLabels: selectedClassLabels.value,
    q: searchText.value.trim(),
    startMs: startCustomMs.value,
    endMs: endCustomMs.value
  };
  try {
    window.localStorage.setItem(ALARM_FILTERS_KEY, JSON.stringify(stored));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

function applyPresetWindow(id: string) {
  const preset = presets.find((entry) => entry.id === id);
  if (!preset) {
    return;
  }

  activePreset.value = preset.id;
  applyRange(preset.ms);
}

// Sets the window from the chosen end (or Now) and the chosen start (or one time range before the end).
function applyRange(presetMs: number) {
  rangeToMs.value = endCustomMs.value ?? Date.now();
  rangeFromMs.value = startCustomMs.value ?? rangeToMs.value - presetMs;
}

function presetMs(): number {
  return presets.find((entry) => entry.id === activePreset.value)?.ms ?? presets[2].ms;
}

function applyStoredWindow(stored: StoredAlarmFilters) {
  endCustomMs.value = stored.endMs;
  // A chosen end always comes with a chosen start.
  startCustomMs.value = stored.startMs
    ?? (stored.endMs !== null ? stored.endMs - (presets.find((entry) => entry.id === stored.preset)?.ms ?? presets[2].ms) : null);
  applyPresetWindow(stored.preset);
}

function applyStoredFilters(stored: StoredAlarmFilters) {
  selectedStates.value = stored.states;
  selectedSourceIds.value = stored.sourceIds;
  selectedClassLabels.value = stored.classLabels;
  searchText.value = stored.q;
  applyStoredWindow(stored);
}

function queryHasFilters(): boolean {
  const query = route.query;
  return typeof query.sourceIds === "string"
    || typeof query.sourceId === "string"
    || typeof query.q === "string"
    || typeof query.range === "string"
    || typeof query.from === "string"
    || typeof query.to === "string"
    || typeof query.state === "string";
}

function readQuery() {
  const stored = readStoredFilters();
  if (!queryHasFilters()) {
    if (stored) {
      applyStoredFilters(stored);
    }
    return;
  }

  // A time range (range=24h) always ends now; a chosen start is `from`, a chosen end `to`
  const preset = presets.find((entry) => entry.id === queryText(route.query.range));
  const from = Number(route.query.from);
  const to = Number(route.query.to);
  if (Number.isFinite(from) && from > 0) {
    startCustomMs.value = from;
    endCustomMs.value = Number.isFinite(to) && to > from ? to : null;
    activePreset.value = preset?.id ?? stored?.preset ?? activePreset.value;
    applyRange(presetMs());
  } else if (preset) {
    startCustomMs.value = null;
    endCustomMs.value = null;
    applyPresetWindow(preset.id);
  } else if (stored) {
    applyStoredWindow(stored);
  }

  if (typeof route.query.state === "string") {
    selectedStates.value = knownStates(route.query.state);
  } else if (stored) {
    selectedStates.value = stored.states;
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
    // The time range of a window that ends now; times only for a chosen start (and end)
    range: startCustomMs.value === null && endCustomMs.value === null ? activePreset.value : "",
    from: startCustomMs.value !== null ? String(startCustomMs.value) : "",
    to: endCustomMs.value !== null ? String(endCustomMs.value) : "",
    q: searchText.value.trim(),
    state: selectedStates.value.join(","),
    sourceIds: selectedSourceIds.value.length > 0 ? selectedSourceIds.value.join(",") : ""
  };
  const current = route.query;
  if (
    queryText(current.range) === next.range
    && queryText(current.from) === next.from
    && queryText(current.to) === next.to
    && queryText(current.q) === next.q
    && queryText(current.state) === next.state
    && queryText(current.sourceIds) === next.sourceIds
    // Left over from a link to another page or from the time before the pages were apart
    && queryText(current.mode) === ""
    && queryText(current.sourceId) === ""
  ) {
    return;
  }

  const target = {
    query: {
      range: next.range || undefined,
      from: next.from || undefined,
      to: next.to || undefined,
      state: next.state || undefined,
      q: next.q || undefined,
      sourceIds: next.sourceIds || undefined
    }
  };
  syncedRoutePath = router.resolve(target).fullPath;
  void router.replace(target);
}

// The class labels as the JSON array the archive takes
function activeClasses(): string | undefined {
  return selectedClassLabels.value.length > 0 ? JSON.stringify(selectedClassLabels.value) : undefined;
}

// The states as the comma-separated list the archive takes
function activeStates(): string | undefined {
  return selectedStates.value.length > 0 ? selectedStates.value.join(",") : undefined;
}

function activeSourceIds(): string | undefined {
  return selectedSourceIds.value.length > 0 ? selectedSourceIds.value.join(",") : undefined;
}

// Folded whenever the filter panel opens, also with a start or end time chosen.
const customRangeOpen = ref(false);

function onRangeToggle(event: Event) {
  customRangeOpen.value = (event.target as HTMLDetailsElement).open;
}

function pad2(value: number): string {
  return String(value).padStart(2, "0");
}

// Value of a datetime-local input: local time to the second.
function toInputValue(ms: number): string {
  const date = new Date(ms);
  return `${date.getFullYear()}-${pad2(date.getMonth() + 1)}-${pad2(date.getDate())}`
    + `T${pad2(date.getHours())}:${pad2(date.getMinutes())}:${pad2(date.getSeconds())}`;
}

function inputTime(event: Event): number | null {
  const value = (event.target as HTMLInputElement).value;
  const ms = value ? new Date(value).getTime() : Number.NaN;
  return Number.isFinite(ms) ? ms : null;
}

function rangeChanged() {
  applyRange(presetMs());
  syncQuery();
  void reloadHistory();
}

// A time range that ends now is either given by a preset (no start chosen), or a window with a start (and an end).
// A chosen end therefore always has a chosen start.

// Back to the time range of the preset, ending now.
function resetRange() {
  startCustomMs.value = null;
  endCustomMs.value = null;
  rangeChanged();
}

// Moves the window by its own length. The result is a window with a chosen start and end, as the time range
// of a preset no longer holds; only a move forward to Now returns to that time range.
function shiftRange(direction: -1 | 1) {
  const span = rangeToMs.value - rangeFromMs.value;
  const now = Date.now();
  const end = rangeToMs.value + direction * span;
  // An end that lands just short of Now (a few seconds, a few percent of the range) counts as Now.
  if (end >= now - Math.min(span * 0.05, 60_000)) {
    const preset = presets.find((entry) => Math.abs(entry.ms - span) < PRESET_MATCH_MS);
    endCustomMs.value = null;
    if (preset) {
      activePreset.value = preset.id;
      startCustomMs.value = null;
    } else {
      startCustomMs.value = now - span;
    }
  } else {
    endCustomMs.value = end;
    startCustomMs.value = end - span;
  }

  rangeChanged();
}

function setEndNow() {
  if (endCustomMs.value === null) {
    return;
  }

  endCustomMs.value = null;
  rangeChanged();
}

function onStartInput(event: Event) {
  const ms = inputTime(event);
  if (ms === null || ms >= rangeToMs.value) {
    // Not before the end: show the window that holds.
    (event.target as HTMLInputElement).value = toInputValue(startCustomMs.value ?? rangeFromMs.value);
    return;
  }

  startCustomMs.value = ms;
  rangeChanged();
}

function onEndInput(event: Event) {
  const ms = inputTime(event);
  if (ms === null || (startCustomMs.value !== null && ms <= startCustomMs.value)) {
    (event.target as HTMLInputElement).value = toInputValue(endCustomMs.value ?? rangeToMs.value);
    return;
  }

  if (ms >= Date.now()) {
    endCustomMs.value = null;
  } else {
    if (startCustomMs.value === null) {
      // Keep the length of the time range: the start moves with the end.
      startCustomMs.value = ms - (rangeToMs.value - rangeFromMs.value);
    }

    endCustomMs.value = ms;
  }

  rangeChanged();
}

function applyPreset(id: string) {
  if (!presets.some((entry) => entry.id === id)) {
    return;
  }

  startCustomMs.value = null;
  endCustomMs.value = null;
  applyPresetWindow(id);
  syncQuery();
  void reloadHistory();
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
    // Keep the last known source names.
  }
}

async function refreshPresentStates() {
  presentStatesAbort?.abort();
  const controller = new AbortController();
  presentStatesAbort = controller;
  try {
    presentStates.value = await getAlarmStates({
      from: rangeFromMs.value,
      to: rangeToMs.value,
      sourceIds: activeSourceIds(),
      classes: activeClasses()
    }, controller.signal);
  } catch {
    // Keep the last known state list.
  } finally {
    if (presentStatesAbort === controller) {
      presentStatesAbort = undefined;
    }
  }
}

async function refreshAlarmClasses() {
  try {
    alarmClasses.value = await getAlarmClasses();
  } catch {
    // Keep the last known class list.
  }
}

function queryParams(offset = 0) {
  return {
    from: rangeFromMs.value,
    to: rangeToMs.value,
    state: activeStates(),
    sourceIds: activeSourceIds(),
    classes: activeClasses(),
    q: searchText.value.trim() || undefined,
    limit: ARCHIVE_PAGE_SIZE,
    offset: offset > 0 ? offset : undefined
  };
}

/**
 * The row that was selected last. It is remembered when the filter changes: it is selected again, with its details, as
 * soon as a filter shows it, and while it is not in the list the list lies at its time. Another selection replaces it,
 * closing the details forgets it.
 */
type RememberedRow = {
  correlationId: string;
  alarmState: string;
  time: number;
  /** Where the row stood in the table when the list last changed, in rows below its upper edge. */
  offset: number | null;
};
let remembered: RememberedRow | null = null;

function remember(row: EventLogItem | undefined) {
  remembered = row?.correlationId
    ? { correlationId: row.correlationId, alarmState: row.alarmState, time: Date.parse(row.time), offset: null }
    : null;
}

// Called before a filter changes the list: the selected row is still where the user left it. The row comes back to the
// same place in the table, also after several filters in which it was not in the list.
function rememberOffset() {
  if (remembered && selectedIndex.value >= 0) {
    remembered.offset = eventTableRef.value?.rowOffsetInView(selectedIndex.value) ?? remembered.offset;
  }
}

// The archive is read in pages, so the position of the row in the new list is counted first: the rows that come before
// the millisecond of the row in the time order. Then the list scrolls there and looks for the row.
type PendingRestore = {
  row: RememberedRow;
  newestFirst: boolean;
  position: number | null;
  scrolled: boolean;
  place: number | "center";
};
let pendingRestore: PendingRestore | null = null;

function archiveTimeOrder(): boolean | null {
  if (sortColumn.value === null) {
    return true;
  }
  return sortColumn.value === "time" ? sortDir.value === "desc" : null;
}

function startRestoreArchive(session: { signal: AbortSignal; isCurrent: () => boolean }) {
  pendingRestore = null;
  const newestFirst = archiveTimeOrder();
  if (!remembered || newestFirst === null) {
    return;
  }
  const pending: PendingRestore = {
    row: remembered,
    newestFirst,
    position: null,
    scrolled: false,
    place: remembered.offset ?? "center"
  };
  pendingRestore = pending;
  void locateRemembered(pending, session);
}

async function locateRemembered(pending: PendingRestore, session: { signal: AbortSignal; isCurrent: () => boolean }) {
  const time = pending.row.time;
  let position = 0;
  try {
    if (pending.newestFirst) {
      // The newer rows come before the row (all of its millisecond follows them)
      const from = Math.max(rangeFromMs.value, time + 1);
      if (from < rangeToMs.value) {
        position = (await getEventLogCount({ ...queryParams(), from }, session.signal)).total;
      }
    } else {
      const to = Math.min(rangeToMs.value, time);
      if (to > rangeFromMs.value) {
        position = (await getEventLogCount({ ...queryParams(), to }, session.signal)).total;
      }
    }
  } catch {
    if (pendingRestore === pending) {
      pendingRestore = null;
    }
    return;
  }
  if (pendingRestore !== pending || !session.isCurrent()) {
    return;
  }
  pending.position = position;
  tryRestoreArchive();
}

function tryRestoreArchive() {
  const pending = pendingRestore;
  if (!pending || pending.position === null || archiveCountStatus.value !== "ready") {
    return;
  }
  const total = archiveTotal.value ?? 0;
  if (total <= 0) {
    pendingRestore = null;
    return;
  }

  const start = Math.min(pending.position, total - 1);
  if (!pending.scrolled) {
    pending.scrolled = true;
    // The table has the new size of the list after the next update
    void nextTick(() => eventTableRef.value?.scrollToIndex(start, pending.place));
  }

  // The rows of the millisecond of the row come first from there on: the row is one of them, or it is not in the list
  for (let index = start; index < total; index += 1) {
    const candidate = archiveRowAt(index);
    if (!candidate) {
      // The page is not loaded yet: the scroll asks for it, else it is read here
      if (archivePageLoading == null) {
        void loadArchivePage(Math.floor(index / ARCHIVE_PAGE_SIZE));
      }
      return;
    }
    const time = Date.parse(candidate.time);
    if (
      time === pending.row.time
      && candidate.correlationId === pending.row.correlationId
      && candidate.alarmState === pending.row.alarmState
    ) {
      pendingRestore = null;
      onSelect(index);
      eventTableRef.value?.scrollToIndex(index, pending.place);
      return;
    }
    if (pending.newestFirst ? time < pending.row.time : time > pending.row.time) {
      break;
    }
  }
  pendingRestore = null;
}

async function reloadHistory() {
  rememberOffset();
  clearSearchTimer();
  loadedSearch = searchText.value.trim();
  syncQuery();
  const session = poll.begin();
  cancelAlarmHistory();
  cancelArchivePages();
  clearArchivePages();
  busy.value = true;
  error.value = "";
  archiveStatus.value = "loading";
  archiveCountStatus.value = "idle";
  archiveCountProgress.value = null;
  archiveTotal.value = null;
  archiveSortedOf.value = null;
  archiveVisibleStart.value = 0;
  archiveVisibleEnd.value = -1;
  selectedIndex.value = -1;
  selectedCorrelationId.value = "";
  startArchiveCount(session);
  void loadArchivePage(0);
  void refreshPresentStates();
  startRestoreArchive(session);

  if (session.isCurrent()) {
    busy.value = false;
    archiveStatus.value = "ready";
  }
}

function startArchiveCount(session: { signal: AbortSignal; isCurrent: () => boolean }) {
  archiveCountStatus.value = "loading";
  archiveTotal.value = null;
  archiveCountProgress.value = null;
  void getEventLogCount(queryParams(), session.signal, (progress) => {
    if (session.isCurrent()) {
      archiveCountProgress.value = progress.count;
    }
  })
    .then((result) => {
      if (!session.isCurrent()) {
        return;
      }

      if (archiveSortedOf.value !== null) {
        archiveSortedOf.value = result.total;
      } else {
        archiveTotal.value = result.total;
      }
      archiveCountStatus.value = "ready";
      archiveStatus.value = "ready";
      if (!archivePageBusy.value) {
        archiveUpdateSeq.value += 1;
      }
    })
    .catch((err) => {
      if (!session.isCurrent() || isCancelledError(err)) {
        return;
      }

      archiveCountStatus.value = "failed";
      archiveStatus.value = "failed";
      error.value = err instanceof Error ? err.message : "Failed to count alarms.";
      if (!archivePageBusy.value) {
        archiveUpdateSeq.value += 1;
      }
    });
}

function clearArchivePages() {
  archivePages.value = new Map();
  archivePageRevision.value += 1;
}

function cancelArchivePages() {
  archivePageGeneration += 1;
  archivePageAbort?.abort();
  archivePageAbort = null;
  archivePageLoading = null;
  archivePageBusy.value = false;
}

function markArchiveLoadFinished() {
  archivePageBusy.value = false;
  archiveUpdateSeq.value += 1;
}

function archiveRowAt(index: number): EventLogItem | undefined {
  const pageIndex = Math.floor(index / ARCHIVE_PAGE_SIZE);
  const rows = archivePages.value.get(pageIndex);
  return rows?.[index % ARCHIVE_PAGE_SIZE];
}

function pagesTouching(start: number, end: number): number[] {
  const total = archiveTotal.value;
  if (total == null || total <= 0 || end < start) {
    return [];
  }

  const lastPage = Math.max(0, Math.ceil(total / ARCHIVE_PAGE_SIZE) - 1);
  const from = Math.max(0, Math.floor(start / ARCHIVE_PAGE_SIZE));
  const to = Math.min(lastPage, Math.floor(end / ARCHIVE_PAGE_SIZE));
  const pages: number[] = [];
  for (let page = from; page <= to; page += 1) {
    pages.push(page);
  }
  return pages;
}

function pagesToKeep(start: number, end: number): number[] {
  const touching = pagesTouching(start, end);
  if (touching.length === 0) {
    return [];
  }

  const total = archiveTotal.value ?? 0;
  const lastPage = Math.max(0, Math.ceil(total / ARCHIVE_PAGE_SIZE) - 1);
  const from = Math.max(0, touching[0] - 1);
  const to = Math.min(lastPage, touching[touching.length - 1] + 1);
  const pages: number[] = [];
  for (let page = from; page <= to; page += 1) {
    pages.push(page);
  }
  return pages;
}

function retainArchivePages(keep: number[]) {
  const keepSet = new Set(keep);
  let changed = false;
  const next = new Map(archivePages.value);
  for (const key of next.keys()) {
    if (!keepSet.has(key)) {
      next.delete(key);
      changed = true;
    }
  }
  if (changed) {
    archivePages.value = next;
    archivePageRevision.value += 1;
  }
}

function expectedPageLength(pageIndex: number): number {
  const total = archiveTotal.value ?? 0;
  return Math.max(0, Math.min(ARCHIVE_PAGE_SIZE, total - pageIndex * ARCHIVE_PAGE_SIZE));
}

/**
 * A page counts as loaded only when it holds every row the total promises. A stream cut off by a
 * scroll leaves a partial page, which must be read again.
 */
function isPageLoaded(pageIndex: number): boolean {
  const rows = archivePages.value.get(pageIndex);
  return rows != null && rows.length >= expectedPageLength(pageIndex);
}

function missingPages(start: number, end: number): number[] {
  return pagesTouching(start, end).filter((page) => !isPageLoaded(page));
}

/** A short answer to a finished read marks the end of the data. The total follows it. */
function trimTotalToPage(pageIndex: number, rowCount: number) {
  if (rowCount >= ARCHIVE_PAGE_SIZE || archiveTotal.value == null) {
    return;
  }

  const end = pageIndex * ARCHIVE_PAGE_SIZE + rowCount;
  if (end < archiveTotal.value) {
    archiveTotal.value = end;
  }
}

function onArchiveRange(start: number, end: number) {
  if (archiveCountStatus.value !== "ready") {
    return;
  }

  archiveVisibleStart.value = start;
  archiveVisibleEnd.value = end;
  const keep = pagesToKeep(start, end);
  retainArchivePages(keep);
  const missing = missingPages(start, end);
  if (missing.length === 0) {
    return;
  }

  // The page in flight is still needed. Restarting it on every scroll step would never finish.
  if (archivePageLoading != null && missing.includes(archivePageLoading)) {
    return;
  }

  const center = Math.floor(((start + end) / 2) / ARCHIVE_PAGE_SIZE);
  const nextPage = missing.includes(center) ? center : missing[0];
  void loadArchivePage(nextPage);
}

/** Drops pages out of view, then loads the next visible page that is still missing. */
function finishArchivePage(pageIndex: number) {
  archivePageLoading = null;
  const keep = new Set(pagesToKeep(archiveVisibleStart.value, archiveVisibleEnd.value));
  keep.add(pageIndex);
  retainArchivePages([...keep]);
  const follow = missingPages(archiveVisibleStart.value, archiveVisibleEnd.value)
    .find((page) => page !== pageIndex);
  if (follow != null && archiveCountStatus.value === "ready") {
    void loadArchivePage(follow);
    return;
  }

  markArchiveLoadFinished();
}

async function loadArchivePage(pageIndex: number) {
  if (pageIndex < 0) {
    return;
  }

  const generation = ++archivePageGeneration;
  archivePageAbort?.abort();
  const controller = new AbortController();
  archivePageAbort = controller;
  archivePageLoading = pageIndex;
  archivePageBusy.value = true;
  try {
    const page = await getEventLogPage(
      { ...queryParams(pageIndex * ARCHIVE_PAGE_SIZE), ...archiveSortParams() },
      controller.signal
    );
    if (generation !== archivePageGeneration) {
      return;
    }

    const next = new Map(archivePages.value);
    next.set(pageIndex, page.items);
    archivePages.value = next;
    archivePageRevision.value += 1;
    // A sorted page knows the size of the sorted range, so the table does not wait for the count.
    if (page.total != null && page.total > 0) {
      archiveTotal.value = page.total;
      archiveSortedOf.value = page.sortedOf != null && page.sortedOf > page.total ? page.sortedOf : null;
      archiveCountStatus.value = "ready";
      archiveStatus.value = "ready";
    }
    trimTotalToPage(pageIndex, page.items.length);
    finishArchivePage(pageIndex);
  } catch (err) {
    if (generation !== archivePageGeneration || isCancelledError(err)) {
      return;
    }

    archivePageLoading = null;
    error.value = err instanceof Error ? err.message : "Failed to load alarms.";
    archiveStatus.value = "failed";
    markArchiveLoadFinished();
  }
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
  pendingRestore = null;
  cancelAlarmHistory();
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === "Escape") {
    if (sourceMenuOpen.value || classMenuOpen.value || stateMenuOpen.value) {
      event.preventDefault();
      sourceMenuOpen.value = false;
      classMenuOpen.value = false;
      stateMenuOpen.value = false;
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
  const rowCount = archiveTotal.value ?? 0;
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
  const row = archiveRowAt(index);
  selectedCorrelationId.value = row?.correlationId ?? "";
  // A row that is picked replaces the one that was remembered, and a restore that is under way
  remember(row);
  pendingRestore = null;
  void loadAlarmHistory();
}

function cancelAlarmHistory() {
  alarmHistoryAbort?.abort();
  alarmHistoryAbort = undefined;
  alarmHistory.value = [];
}

async function loadAlarmHistory() {
  cancelAlarmHistory();
  if (archiveStatus.value !== "ready") {
    return;
  }

  const row = selectedItem.value;
  if (!row?.correlationId) {
    return;
  }

  const controller = new AbortController();
  alarmHistoryAbort = controller;

  try {
    const page = await getAlarmHistory({
      correlationId: row.correlationId,
      sourceId: row.sourceId > 0 ? row.sourceId : undefined,
      from: rangeFromMs.value,
      to: rangeToMs.value
    }, controller.signal);
    if (alarmHistoryAbort !== controller) {
      return;
    }

    alarmHistory.value = page.items;
  } catch (err) {
    if (alarmHistoryAbort !== controller) {
      return;
    }

    alarmHistory.value = [];
    if (!isCancelledError(err)) {
      error.value = err instanceof Error ? err.message : "Failed to load the alarm history.";
    }
  }
}
</script>
