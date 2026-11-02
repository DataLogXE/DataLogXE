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
  <div class="chart-picker">
    <div class="chart-picker-bar">
      <select v-model.number="sourceId" class="chart-picker-source" aria-label="Data source">
        <option v-for="source in sources" :key="source.id" :value="source.id">{{ source.name }}</option>
      </select>
      <div class="tag-filter-field chart-picker-filter">
        <input
          v-model="filter"
          type="text"
          placeholder="Name, display name, address, or comment"
          data-hint="Filters name, display name, address, and comment. Wildcards * and ? are supported."
          aria-label="Filter data tags"
          autocomplete="off"
          spellcheck="false"
        />
        <button
          v-if="filter"
          class="tag-icon-btn field-clear"
          type="button"
          data-hint="Clear filter"
          aria-label="Clear filter"
          @click="filter = ''"
        >
          <svg viewBox="0 0 16 16" aria-hidden="true">
            <path d="M4 4l8 8M12 4L4 12" />
          </svg>
        </button>
      </div>
    </div>

    <ul class="chart-picker-list" aria-label="Active data tags">
      <li
        v-for="tag in items"
        :key="tag.id"
        class="chart-picker-row"
        :class="{ 'is-used': used.has(tag.id) }"
        :data-hint="tag.address"
        @dblclick="add(tag)"
      >
        <span class="chart-picker-name">{{ tag.name }}</span>
        <span v-if="tag.displayName && tag.displayName !== tag.name" class="chart-picker-display">{{ tag.displayName }}</span>
        <button
          class="chart-icon-btn chart-picker-add"
          type="button"
          :disabled="used.has(tag.id) || full"
          :aria-label="`Add ${tag.name}`"
          :data-hint="used.has(tag.id) ? 'Already in the chart' : 'Add to the chart'"
          @click="add(tag)"
        >
          <svg viewBox="0 0 16 16" aria-hidden="true">
            <path d="M8 3.5v9M3.5 8h9" />
          </svg>
        </button>
      </li>
      <li v-if="!loading && !items.length && !error" class="chart-picker-empty hint">
        {{ filter ? "No active data tags match the filter." : "This data source has no active data tags." }}
      </li>
    </ul>

    <div class="chart-picker-foot">
      <span v-if="error" class="error-text">{{ error }}</span>
      <span v-else class="hint">{{ status }}</span>
      <button v-if="canLoadMore" class="btn secondary btn-sm" type="button" :disabled="loading" @click="loadMore">
        Load more
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from "vue";
import { getDataSources, getDataTags, isAbortError } from "../api";
import { formatInteger } from "../format";
import type { DataSourceListItem, DataTagListItem } from "../types";

defineProps<{
  used: ReadonlySet<number>;
  /** The chart has the most data tags it can show. */
  full: boolean;
}>();

const emit = defineEmits<{
  add: [tag: PickedTag];
}>();

const PAGE_SIZE = 150;
const MAX_PAGES = 10;
const FILTER_DEBOUNCE_MS = 300;
const PREPARE_RETRY_MS = 500;

const sources = ref<DataSourceListItem[]>([]);
const sourceId = ref<number | null>(lastSourceId);
const filter = ref("");
const items = ref<DataTagListItem[]>([]);
const total = ref(0);
const pages = ref(0);
const loading = ref(false);
const preparing = ref<number | null>(null);
const error = ref("");

let generation = 0;
let abort: AbortController | null = null;
let filterTimer: number | undefined;
let retryTimer: number | undefined;

const canLoadMore = computed(() => items.value.length < total.value && pages.value < MAX_PAGES);

const status = computed(() => {
  if (preparing.value != null) {
    return `Preparing tag list … ${preparing.value} %`;
  }
  if (loading.value && !items.value.length) {
    return "Loading …";
  }
  if (items.value.length < total.value && pages.value >= MAX_PAGES) {
    return `${formatInteger(items.value.length)} of ${formatInteger(total.value)} shown · refine the filter to see more`;
  }
  return total.value === 1 ? "1 active tag" : `${formatInteger(total.value)} active tags`;
});

function sourceName(id: number): string {
  return sources.value.find((source) => source.id === id)?.name ?? "";
}

function add(tag: DataTagListItem) {
  if (sourceId.value == null) {
    return;
  }
  emit("add", {
    dataTagId: tag.id,
    dataSourceId: sourceId.value,
    sourceName: sourceName(sourceId.value),
    tagName: tag.name,
    displayName: tag.displayName ?? ""
  });
}

async function loadPage(page: number, gen: number) {
  const id = sourceId.value;
  if (id == null) {
    return;
  }
  abort?.abort();
  const controller = new AbortController();
  abort = controller;
  loading.value = true;
  try {
    const data = await getDataTags(
      id,
      { q: filter.value, activeOnly: true, offset: page * PAGE_SIZE, limit: PAGE_SIZE },
      controller.signal
    );
    if (gen !== generation) {
      return;
    }
    if (data.preparing) {
      // The server builds the tag list of the data source first
      preparing.value = Math.max(0, Math.min(100, data.preparePercent ?? 0));
      retryTimer = window.setTimeout(() => void loadPage(page, gen), PREPARE_RETRY_MS);
      return;
    }
    preparing.value = null;
    total.value = data.total;
    items.value = page === 0 ? data.items : [...items.value, ...data.items];
    pages.value = page + 1;
    error.value = "";
    loading.value = false;
  } catch (err) {
    if (gen !== generation || isAbortError(err)) {
      return;
    }
    error.value = err instanceof Error ? err.message : "Unable to load the data tags.";
    loading.value = false;
  }
}

function reload() {
  generation += 1;
  window.clearTimeout(retryTimer);
  items.value = [];
  total.value = 0;
  pages.value = 0;
  preparing.value = null;
  error.value = "";
  void loadPage(0, generation);
}

function loadMore() {
  if (!loading.value && canLoadMore.value) {
    void loadPage(pages.value, generation);
  }
}

watch(sourceId, (id) => {
  if (id != null) {
    lastSourceId = id;
  }
  reload();
});

watch(filter, () => {
  window.clearTimeout(filterTimer);
  filterTimer = window.setTimeout(reload, FILTER_DEBOUNCE_MS);
});

onMounted(async () => {
  try {
    sources.value = await getDataSources();
  } catch (err) {
    error.value = err instanceof Error ? err.message : "Unable to load the data sources.";
    return;
  }
  const known = sources.value.some((source) => source.id === sourceId.value);
  if (!known) {
    // A data source of the plant is more likely wanted than the system values
    sourceId.value = (sources.value.find((source) => !source.isSys) ?? sources.value[0])?.id ?? null;
  } else {
    reload();
  }
});

onUnmounted(() => {
  generation += 1;
  abort?.abort();
  window.clearTimeout(filterTimer);
  window.clearTimeout(retryTimer);
});
</script>

<script lang="ts">
export interface PickedTag {
  dataTagId: number;
  dataSourceId: number;
  sourceName: string;
  tagName: string;
  displayName: string;
}

// The data source the picker showed last, for the next time it opens
let lastSourceId: number | null = null;
</script>
