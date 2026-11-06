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

<!-- Settings > System log: the log of the whole system for the administrator, with its downloads -->
<template>
  <div class="system-log">
    <AppLogView
      v-model:text="filterText"
      v-model:level="minLevel"
      id-prefix="system-log"
      show-source
      :load="getSystemLog"
      :extra-params="extraParams"
      :source-names="sourceNames"
      @correlation="correlation = $event"
    >
      <template #filters>
        <select v-model="component" class="system-log-select" aria-label="Component" data-hint="Part of the application">
          <option value="">All components</option>
          <option v-for="option in componentOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
        <select v-model="source" class="system-log-select" aria-label="Source" data-hint="Data source of the events">
          <option value="">All sources</option>
          <option value="0">Application only</option>
          <option v-for="item in sourceList" :key="item.id" :value="String(item.id)">{{ item.name }}</option>
        </select>
        <span v-if="correlation" class="system-log-chip">
          Operation {{ shortId(correlation) }}
          <button class="tag-icon-btn" type="button" aria-label="Show every operation" data-hint="Show every operation" @click="correlation = ''">
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M4 4l8 8M12 4L4 12" />
            </svg>
          </button>
        </span>
      </template>
      <template #actions>
        <div class="system-log-actions">
          <button class="btn secondary" type="button" data-hint="The log as CSV or NDJSON" @click="dialog = 'log'">Export</button>
          <button class="btn secondary" type="button" data-hint="Log, log files and state of the system as one ZIP file for a bug report" @click="dialog = 'bundle'">
            Support bundle
          </button>
        </div>
      </template>
    </AppLogView>

    <SystemLogDownloadDialog
      v-if="dialog"
      :mode="dialog"
      :filters="currentFilters"
      :filtered="filtered"
      @close="dialog = null"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import AppLogView from "./AppLogView.vue";
import SystemLogDownloadDialog from "./SystemLogDownloadDialog.vue";
import { getDataSources, getSystemLog } from "../api";
import type { DataSourceListItem } from "../types";

// The components the server sorts the log into (EventLogMapper)
const componentOptions = [
  { value: "service", label: "Service" },
  { value: "api", label: "Web API" },
  { value: "archive", label: "Archive" },
  { value: "postgres", label: "Database" },
  { value: "opcua", label: "OPC UA" }
];

const filterText = ref("");
const minLevel = ref("");
const component = ref("");
const source = ref("");
const correlation = ref("");
const dialog = ref<"log" | "bundle" | null>(null);
const sourceList = ref<DataSourceListItem[]>([]);

const sourceNames = computed(() => new Map(sourceList.value.map((s) => [s.id, s.name])));
const extraParams = computed(() => ({
  component: component.value || undefined,
  source: source.value === "" ? undefined : source.value,
  correlation: correlation.value || undefined
}));
const currentFilters = computed(() => ({
  ...extraParams.value,
  level: minLevel.value || undefined,
  q: filterText.value.trim() || undefined
}));
const filtered = computed(() => Object.values(currentFilters.value).some((v) => v !== undefined));

function shortId(id: string): string {
  return id.length > 12 ? `${id.slice(0, 8)}…` : id;
}

onMounted(async () => {
  try {
    sourceList.value = (await getDataSources()).slice().sort((a, b) => a.name.localeCompare(b.name));
  } catch {
    // The log shows the ids then
  }
});
</script>
