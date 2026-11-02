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
  <div v-if="visible.length > 0 || others.length > 0" class="query-activity-host" aria-live="polite">
    <div v-for="item in visible" :key="item.id" class="query-activity-card" role="status">
      <div class="query-activity-head">
        <div class="spinner" aria-hidden="true"></div>
        <div class="query-activity-text">
          <strong :data-hint="item.label">{{ item.label }}</strong>
          <span class="query-activity-phase">
            {{ item.phase }}<template v-if="item.count != null && item.count > 0"> · {{ formatInteger(item.count) }}</template>
          </span>
        </div>
        <QueryCostBadge v-if="item.cost" :cost="item.cost" />
        <span class="query-activity-time">{{ formatElapsed(now - item.startedAt) }}</span>
        <button class="chart-icon-btn" type="button" data-hint="Cancel" :aria-label="`Cancel: ${item.label}`" @click="item.cancel()">
          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4L4 12" /></svg>
        </button>
      </div>
      <div v-if="item.percent != null" class="query-activity-bar" aria-hidden="true">
        <span :style="{ width: `${Math.max(2, Math.min(100, item.percent))}%` }"></span>
      </div>
    </div>
    <div v-if="others.length > 0" class="query-activity-card query-activity-others" role="status">
      <strong>Queries of other users and tabs</strong>
      <div v-for="item in others" :key="item.key" class="query-activity-head">
        <QueryCostBadge :cost="item.cost" />
        <div class="query-activity-text">
          <span :data-hint="item.kind">{{ kindLabel(item.kind) }}<template v-if="item.clients > 1"> · {{ item.clients }} clients</template></span>
          <span class="query-activity-phase">{{ item.phase }}</span>
        </div>
        <span class="query-activity-time">{{ formatElapsed(item.elapsedMs) }}</span>
        <button
          v-if="isAdmin"
          class="chart-icon-btn"
          type="button"
          data-hint="Stop for all its users"
          :aria-label="`Stop: ${kindLabel(item.kind)}`"
          @click="stopping = item"
        >
          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4L4 12" /></svg>
        </button>
      </div>
    </div>
  </div>
  <ConfirmDialog
    v-if="stopping"
    class="query-stop-overlay"
    title="Stop the query?"
    :message="`${kindLabel(stopping.kind)} (${stopping.cost}) runs for ${formatElapsed(stopping.elapsedMs)}. Its users get a note that an administrator stopped it.`"
    confirm-label="Stop"
    :busy="stopBusy"
    :error="stopError"
    @confirm="stop"
    @close="closeStop"
  />
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import { stopRunningQuery, type RunningQuery } from "../api";
import { formatInteger } from "../format";
import { useQueryActivities } from "../queryActivity";
import { isAdmin } from "../session";
import { useSystemLoad } from "../systemLoad";
import ConfirmDialog from "./ConfirmDialog.vue";
import QueryCostBadge from "./QueryCostBadge.vue";

// Fast queries never show up; the panel only reports a query the user is actually waiting for.
const SHOW_AFTER_MS = 5000;

const activities = useQueryActivities();
const now = ref(Date.now());
let timer: number | undefined;

const visible = computed(() => activities.value.filter((item) => now.value - item.startedAt >= SHOW_AFTER_MS));

// The queries of the other tabs and users, from the server; the own ones show above
const { running, refresh } = useSystemLoad();
const others = computed(() => running.value.filter((item) => !item.mine && item.elapsedMs >= SHOW_AFTER_MS));

const KIND_LABELS: Record<string, string> = {
  "statistics": "Statistics",
  "raw-values": "Raw value export",
  "raw-count": "Raw value count",
  "plot-points": "Chart data",
  "plot-preview": "Chart preview",
  "value-at": "Cursor value",
  "events/alarm-counts": "Alarm statistics",
  "events/alarm-history": "Alarm history",
  "events/page": "Alarm list",
  "events/count": "Alarm count",
  "events/summary": "Alarm summary",
  "alarm-states": "Alarm states"
};

function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind;
}

const stopping = ref<RunningQuery | null>(null);
const stopBusy = ref(false);
const stopError = ref("");

async function stop() {
  if (!stopping.value) {
    return;
  }
  stopBusy.value = true;
  stopError.value = "";
  try {
    await stopRunningQuery(stopping.value.key);
    closeStop();
    void refresh();
  } catch (error) {
    stopError.value = error instanceof Error ? error.message : String(error);
  } finally {
    stopBusy.value = false;
  }
}

function closeStop() {
  stopping.value = null;
  stopError.value = "";
}

watch(
  () => activities.value.length > 0,
  (running) => {
    if (running && timer === undefined) {
      now.value = Date.now();
      timer = window.setInterval(() => {
        now.value = Date.now();
      }, 500);
    } else if (!running && timer !== undefined) {
      window.clearInterval(timer);
      timer = undefined;
    }
  },
  { immediate: true }
);

onUnmounted(() => {
  if (timer !== undefined) {
    window.clearInterval(timer);
  }
});

function formatElapsed(ms: number): string {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, "0")}`;
}
</script>
