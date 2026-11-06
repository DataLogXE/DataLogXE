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

<!-- The downloads of the system log: the log itself (CSV or NDJSON) or the support bundle (ZIP) -->
<template>
  <div class="overlay chart-dialog-overlay" @click.self="!busy && emit('close')">
    <form
      class="overlay-card chart-dialog system-log-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="system-log-dialog-title"
      :aria-busy="busy"
      @submit.prevent="submit"
      @keydown.esc.prevent="!busy && emit('close')"
    >
      <header>
        <strong id="system-log-dialog-title">{{ mode === "bundle" ? "Support bundle" : "Export log" }}</strong>
        <p class="hint">
          {{
            mode === "bundle"
              ? "One ZIP file for a bug report: the log, the log files, versions and the state of the database and the data sources. Passwords and keys are left out."
              : "The events of the system log as a file. Times are in UTC."
          }}
        </p>
      </header>

      <div class="system-log-dialog-choice">
        <span id="system-log-range" class="chart-export-label">Range</span>
        <div class="segmented" role="radiogroup" aria-labelledby="system-log-range">
          <label v-for="option in rangeOptions" :key="option.hours" class="segmented-option">
            <input v-model="hours" class="sr-only" type="radio" :value="option.hours" />
            {{ option.label }}
          </label>
        </div>
      </div>

      <template v-if="mode === 'log'">
        <div class="system-log-dialog-choice">
          <span id="system-log-format" class="chart-export-label">Format</span>
          <div class="segmented" role="radiogroup" aria-labelledby="system-log-format">
            <label class="segmented-option" data-hint="For a spreadsheet">
              <input v-model="format" class="sr-only" type="radio" value="csv" />
              CSV
            </label>
            <label class="segmented-option" data-hint="One JSON object per line, with every property, for tools">
              <input v-model="format" class="sr-only" type="radio" value="ndjson" />
              NDJSON
            </label>
          </div>
        </div>
        <label v-if="filtered" class="chart-export-option">
          <input v-model="useFilters" type="checkbox" />
          Only events that match the current filters
        </label>
      </template>

      <p v-if="error" class="error-text">{{ error }}</p>

      <footer class="system-log-dialog-actions">
        <button class="btn secondary" type="button" :disabled="busy" @click="emit('close')">Cancel</button>
        <button class="btn" type="submit" :disabled="busy">
          <span v-if="busy" class="spinner spinner-sm" aria-hidden="true"></span>
          Download
        </button>
      </footer>
    </form>
  </div>
</template>

<script setup lang="ts">
import { ref } from "vue";
import { checkDiagnostics, checkSystemLogExport, diagnosticsUrl, systemLogExportUrl, type SystemLogExportParams } from "../api";
import { decimalComma, downloadUrl } from "../chart/csvExport";

const props = defineProps<{
  mode: "log" | "bundle";
  filters: Record<string, string | undefined>;
  filtered: boolean;
}>();
const emit = defineEmits<{ close: [] }>();

const rangeOptions = [
  ...(props.mode === "log" ? [{ hours: 1, label: "1 hour" }] : []),
  { hours: 24, label: "24 hours" },
  { hours: 24 * 7, label: "7 days" },
  { hours: 24 * 14, label: "14 days" }
];

const hours = ref(24);
const format = ref<"csv" | "ndjson">("csv");
const useFilters = ref(true);
const busy = ref(false);
const error = ref("");

async function submit() {
  busy.value = true;
  error.value = "";
  try {
    if (props.mode === "bundle") {
      await checkDiagnostics(hours.value);
      downloadUrl(diagnosticsUrl(hours.value), "");
    } else {
      const to = new Date();
      const from = new Date(to.getTime() - hours.value * 3600_000);
      const params: SystemLogExportParams = {
        ...(useFilters.value ? props.filters : {}),
        from: from.toISOString(),
        to: to.toISOString(),
        format: format.value,
        decimalComma: decimalComma()
      };
      await checkSystemLogExport(params);
      downloadUrl(systemLogExportUrl(params), "");
    }
    emit("close");
  } catch (e) {
    error.value = e instanceof Error ? e.message : "The download could not be started.";
  } finally {
    busy.value = false;
  }
}
</script>
