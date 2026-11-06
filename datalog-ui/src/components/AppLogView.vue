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

<!-- An application log: a filter bar, one line an event (newest first), live and paged back over the two weeks
     it is kept. The data source log and the system log differ only in what they read and show around it. -->
<template>
  <div class="app-log">
    <div class="tag-bar">
      <div class="tag-filter">
        <div class="tag-filter-field">
          <input
            v-model="filterText"
            type="text"
            placeholder="Message or exception"
            data-hint="Filters message and exception. Wildcards * and ? are supported."
            aria-label="Filter by message or exception"
            autocomplete="off"
            spellcheck="false"
          />
          <button
            v-if="filterText"
            class="tag-icon-btn field-clear"
            type="button"
            data-hint="Clear filter"
            aria-label="Clear filter"
            @click="filterText = ''"
          >
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M4 4l8 8M12 4L4 12" />
            </svg>
          </button>
        </div>
      </div>
      <div class="segmented" role="radiogroup" aria-label="Minimum level">
        <label v-for="option in levelOptions" :key="option.value" class="segmented-option" :data-hint="option.hint">
          <input
            class="sr-only"
            type="radio"
            :name="`${idPrefix}-level`"
            :value="option.value"
            :checked="minLevel === option.value"
            @change="minLevel = option.value"
          />
          {{ option.label }}
        </label>
      </div>
      <slot name="filters" />
      <div class="source-log-live">
        <button
          class="tag-switch"
          type="button"
          :class="{ on: live }"
          :aria-pressed="live"
          aria-label="Show new events as they come"
          data-hint="Show new events as they come"
          @click="live = !live"
        >
          <span class="tag-switch-knob" aria-hidden="true">
            <svg viewBox="0 0 16 16">
              <circle cx="8" cy="8" r="3.2" />
            </svg>
          </span>
        </button>
        <span class="source-log-live-label">Live</span>
      </div>
      <slot name="actions" />
    </div>

    <p v-if="error" class="error-text">{{ error }}</p>

    <div ref="scrollRef" class="source-log-scroll">
      <ol v-if="items.length" class="source-log" :class="{ 'with-source': showSource }">
        <li
          v-for="item in items"
          :key="item.time + item.message"
          :class="[`level-${levelClass(item.level)}`, { open: expanded.has(item.time), expandable: hasDetails(item) }]"
        >
          <button
            class="source-log-row"
            type="button"
            :disabled="!hasDetails(item)"
            :aria-expanded="hasDetails(item) ? expanded.has(item.time) : undefined"
            @click="toggle(item)"
          >
            <span class="source-log-time">{{ formatTime(item.time) }}</span>
            <span class="source-log-level">{{ levelLabel(item.level) }}</span>
            <span v-if="showSource" class="source-log-source" :title="sourceLabel(item)">{{ sourceLabel(item) }}</span>
            <span class="source-log-message">{{ displayMessage ? displayMessage(item.message) : item.message }}</span>
          </button>
          <div v-if="expanded.has(item.time)" class="source-log-details">
            <pre v-if="item.exception">{{ item.exception }}</pre>
            <pre v-if="formatProps(item.props)">{{ formatProps(item.props) }}</pre>
            <p v-if="showSource && item.correlationId" class="source-log-correlation">
              Correlation ID
              <button class="link-btn" type="button" data-hint="Show every event of this operation" @click="emit('correlation', item.correlationId)">
                {{ item.correlationId }}
              </button>
            </p>
          </div>
        </li>
      </ol>
      <div v-else-if="!loading" class="empty">
        {{ filtered ? "No events match the filter." : "No events in the last two weeks." }}
      </div>

      <div class="source-log-more">
        <span v-if="loading" class="source-log-loading">
          <span class="spinner spinner-sm" aria-hidden="true"></span>
          {{ searchingBack ? `Searching back to ${formatDay(nextBefore)} …` : "Loading …" }}
        </span>
        <button v-else-if="nextBefore && items.length" class="btn secondary" type="button" @click="loadOlder">
          Load older
        </button>
        <span v-else-if="items.length" class="source-log-end">Start of the log (kept two weeks)</span>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import { createPollSession, isAbortError } from "../api";
import { formatDateTime, formatDateTimeMs } from "../format";
import type { AppLogParams, SourceLogItem, SourceLogPage } from "../types";

const props = withDefaults(
  defineProps<{
    load: (params: AppLogParams, signal: AbortSignal) => Promise<SourceLogPage>;
    /** Filters besides level and text; a change reloads. */
    extraParams?: Record<string, string | number | undefined>;
    /** Shows the component and source of each event (the system log). */
    showSource?: boolean;
    sourceNames?: Map<number, string>;
    displayMessage?: (message: string) => string;
    /** Properties left out of the details, for example the name of the source already in the title. */
    hiddenProps?: string[];
    /** Changes reload the log, for example the id of the data source. */
    reloadKey?: string | number;
    idPrefix?: string;
  }>(),
  {
    extraParams: () => ({}),
    showSource: false,
    sourceNames: () => new Map(),
    displayMessage: undefined,
    hiddenProps: () => [],
    reloadKey: "",
    idPrefix: "app-log"
  }
);

const emit = defineEmits<{ correlation: [id: string] }>();
const filterText = defineModel<string>("text", { default: "" });
const minLevel = defineModel<string>("level", { default: "" });

const LIVE_INTERVAL_MS = 5000;
const FILTER_DELAY_MS = 300;
// Empty days that one "Load older" skips before it gives up; the log is kept 14 days
const MAX_EMPTY_WINDOWS = 15;

const levelOptions = [
  { value: "", label: "All", hint: "Every event" },
  { value: "Warning", label: "Warnings", hint: "Warnings and errors" },
  { value: "Error", label: "Errors", hint: "Errors only" }
];

const items = ref<SourceLogItem[]>([]);
const nextBefore = ref<string | null>(null);
const loading = ref(false);
const searchingBack = ref(false);
const error = ref("");
const live = ref(true);
const expanded = ref(new Set<string>());
const scrollRef = ref<HTMLElement | null>(null);

const filtered = computed(
  () => Boolean(filterText.value || minLevel.value || Object.values(props.extraParams).some((v) => v !== undefined && v !== ""))
);

const pageSession = createPollSession();
const liveSession = createPollSession();
let liveTimer: number | undefined;
let filterTimer: number | undefined;

function levelClass(level: string): string {
  switch (level) {
    case "Fatal":
    case "Error":
      return "error";
    case "Warning":
      return "warning";
    case "Verbose":
    case "Debug":
      return "debug";
    default:
      return "info";
  }
}

function levelLabel(level: string): string {
  return level === "Information" ? "Info" : level;
}

function formatTime(time: string): string {
  return formatDateTimeMs(new Date(time));
}

function formatDay(time: string | null): string {
  return time ? formatDateTime(new Date(time)) : "";
}

function sourceLabel(item: SourceLogItem): string {
  if (item.sourceId) {
    return props.sourceNames.get(item.sourceId) ?? `#${item.sourceId}`;
  }
  return item.component || "service";
}

function hasDetails(item: SourceLogItem): boolean {
  return Boolean(item.exception || formatProps(item.props) || (props.showSource && item.correlationId));
}

function formatProps(value: string | undefined): string {
  if (!value) {
    return "";
  }
  try {
    return Object.entries(JSON.parse(value) as Record<string, string>)
      .filter(([key]) => !props.hiddenProps.includes(key))
      .map(([key, entry]) => `${key}: ${entry}`)
      .join("\n");
  } catch {
    return value;
  }
}

function toggle(item: SourceLogItem) {
  const next = new Set(expanded.value);
  if (!next.delete(item.time)) {
    next.add(item.time);
  }
  expanded.value = next;
}

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : "Unknown error";
}

function filterParams(): AppLogParams {
  return { ...props.extraParams, level: minLevel.value || undefined, q: filterText.value.trim() || undefined };
}

/** Reads pages back from before until one holds events, skipping the quiet days. */
async function loadPage(before: string | undefined, append: boolean) {
  const session = pageSession.begin();
  liveSession.abort();
  loading.value = true;
  searchingBack.value = false;
  try {
    let cursor = before;
    for (let windows = 0; ; windows++) {
      const page = await props.load({ ...filterParams(), before: cursor }, session.signal);
      if (!session.isCurrent()) {
        return;
      }
      if (windows === 0 && !append) {
        items.value = [];
        expanded.value = new Set();
      }
      items.value = items.value.concat(page.items);
      nextBefore.value = page.nextBefore;
      error.value = "";
      if (page.items.length || !page.nextBefore || windows >= MAX_EMPTY_WINDOWS) {
        break;
      }
      cursor = page.nextBefore;
      searchingBack.value = true;
    }
  } catch (e) {
    if (session.isCurrent() && !isAbortError(e)) {
      error.value = errorText(e);
    }
  } finally {
    if (session.isCurrent()) {
      loading.value = false;
      searchingBack.value = false;
    }
  }
}

function reload() {
  scrollRef.value?.scrollTo({ top: 0 });
  void loadPage(undefined, false);
}

function loadOlder() {
  if (nextBefore.value) {
    void loadPage(nextBefore.value, true);
  }
}

// New events come in at the top while the list is scrolled to it; further down they would move the rows away
async function pollLive() {
  if (!live.value || loading.value || document.hidden || (scrollRef.value?.scrollTop ?? 0) > 4) {
    return;
  }
  if (!items.value.length) {
    reload();
    return;
  }

  const session = liveSession.begin();
  try {
    const page = await props.load({ ...filterParams(), after: items.value[0].time }, session.signal);
    if (!session.isCurrent()) {
      return;
    }
    if (page.truncated) {
      reload();
    } else if (page.items.length) {
      items.value = page.items.concat(items.value);
    }
    error.value = "";
  } catch (e) {
    if (session.isCurrent() && !isAbortError(e)) {
      error.value = errorText(e);
    }
  }
}

watch(minLevel, reload);
watch(filterText, () => {
  window.clearTimeout(filterTimer);
  filterTimer = window.setTimeout(reload, FILTER_DELAY_MS);
});
watch(() => JSON.stringify(props.extraParams), reload);
watch(() => props.reloadKey, reload);

onMounted(() => {
  reload();
  liveTimer = window.setInterval(() => void pollLive(), LIVE_INTERVAL_MS);
});

onUnmounted(() => {
  window.clearInterval(liveTimer);
  window.clearTimeout(filterTimer);
  pageSession.abort();
  liveSession.abort();
});
</script>
