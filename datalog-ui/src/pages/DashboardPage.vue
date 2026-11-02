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
      <div class="dash-title">
        <h1>Dashboard</h1>
        <span v-if="dashboard || pollError" class="dash-health" :class="`is-${health.tone}`" role="status" :data-hint="pollError || undefined">
          <span class="dash-dot" aria-hidden="true" />{{ health.text }}
        </span>
      </div>
      <span class="hint dash-stats">{{ statusText }}</span>
    </div>
    <div class="page-body">
    <div class="dash">
    <section class="dash-kpis" aria-label="Key figures">
      <component
        :is="rateChart ? 'button' : 'div'"
        class="dash-tile"
        :class="{ 'is-action': rateChart }"
        :type="rateChart ? 'button' : undefined"
        :data-hint="rateChart ? 'Chart of the values per second' : undefined"
        @click="rateChart && (showRateChart = true)"
      >
        <div class="dash-label">Values / s<ChartGlyph v-if="rateChart" /></div>
        <div class="dash-value">{{ formatRate(dashboard?.valuesPerSec) }}</div>
        <TrendSparkline class="dash-spark is-accent" :values="dashboard?.valuesPerSecSeries ?? []" />
      </component>

      <component
        :is="loadChart ? 'button' : 'div'"
        class="dash-tile"
        :class="{ 'is-action': loadChart }"
        :type="loadChart ? 'button' : undefined"
        :data-hint="loadChart ? 'Chart of the system load' : undefined"
        @click="loadChart && (showLoadChart = true)"
      >
        <div class="dash-label">System load<ChartGlyph v-if="loadChart" /></div>
        <div class="dash-value">{{ formatPercent(dashboard?.systemLoadPercent) }}</div>
        <div v-if="dashboard?.systemLoadPercent != null" class="dash-meter" :class="`is-${loadTone}`" aria-hidden="true">
          <span :style="{ width: `${loadWidth}%` }" />
        </div>
        <TrendSparkline class="dash-spark is-compact" :class="`is-${loadTone}`" :values="dashboard?.systemLoadSeries ?? []" :max="100" />
      </component>

      <component
        :is="databaseChart ? 'button' : 'div'"
        class="dash-tile"
        :class="{ 'is-action': databaseChart }"
        :type="databaseChart ? 'button' : undefined"
        :data-hint="databaseChart ? 'Chart of the database size' : undefined"
        @click="databaseChart && (showDatabaseChart = true)"
      >
        <div class="dash-label">Database<ChartGlyph v-if="databaseChart" /></div>
        <div class="dash-value">{{ formatGb(dashboard?.databaseDiskSizeGb) }}<span v-if="dashboard?.databaseDiskSizeGb != null" class="dash-unit">GB</span></div>
        <div class="dash-foot">{{ growthText }}</div>
      </component>

      <component
        :is="valueCountChart ? 'button' : 'div'"
        class="dash-tile"
        :class="{ 'is-action': valueCountChart }"
        :type="valueCountChart ? 'button' : undefined"
        :data-hint="valueCountChart ? 'Chart of the values total' : undefined"
        @click="valueCountChart && (showValueCountChart = true)"
      >
        <div class="dash-label">Values total<ChartGlyph v-if="valueCountChart" /></div>
        <div class="dash-value">{{ formatMio(dashboard?.valueCountSumMio) }}<span v-if="dashboard?.valueCountSumMio != null" class="dash-unit">M</span></div>
        <div class="dash-foot" data-hint="Active tags of all active data sources">{{ formatCount(dashboard?.activeTags) }} active tags</div>
      </component>
    </section>

    <section class="dash-panels">
      <div class="dash-panel">
        <div class="dash-panel-head">
          <h2>Data sources <span class="dash-count">{{ formatCount(dashboard?.total) }}</span></h2>
          <router-link class="dash-more" to="/datasources">All sources</router-link>
        </div>
        <div class="dash-stack" role="img" :aria-label="stackLabel">
          <span v-for="part in stackParts" :key="part.key" :class="`is-${part.tone}`" :style="{ flexGrow: part.count }" />
        </div>
        <ul class="dash-legend">
          <li v-for="part in legendParts" :key="part.key">
            <span class="dash-dot" :class="`is-${part.tone}`" aria-hidden="true" />{{ formatCount(part.count) }} {{ part.label }}
          </li>
        </ul>
      </div>

      <div class="dash-panel">
        <div class="dash-panel-head">
          <h2>Alarms</h2>
          <router-link class="dash-more" to="/alarms/pending">Pending alarms</router-link>
        </div>
        <div class="dash-figures">
          <router-link class="dash-figure" :class="{ 'is-danger': (dashboard?.pendingAlarms ?? 0) > 0 }" to="/alarms/pending">
            <span class="dash-value">{{ formatCount(dashboard?.pendingAlarms) }}</span>
            <span class="dash-foot">pending</span>
          </router-link>
          <router-link class="dash-figure" :class="{ 'is-warn': (dashboard?.unackedAlarms ?? 0) > 0 }" to="/alarms/pending">
            <span class="dash-value">{{ formatCount(dashboard?.unackedAlarms) }}</span>
            <span class="dash-foot">not acknowledged</span>
          </router-link>
          <router-link class="dash-figure" :to="{ path: '/alarms/history', query: { range: '24h', state: 'Incoming' } }">
            <span class="dash-value">{{ formatCount(dashboard?.incomingAlarms24h) }}</span>
            <span class="dash-foot">came in 24 h</span>
          </router-link>
        </div>
      </div>
    </section>

    <section v-if="sortedSources.length" class="dash-panel dash-sources" aria-label="State of the data sources">
      <div class="dash-source-row dash-source-head" aria-hidden="true">
        <span>Source</span><span>State</span><span>Values / s</span><span class="dash-num">Clock</span>
      </div>
      <router-link
        v-for="source in sortedSources"
        :key="source.id"
        class="dash-source-row"
        :to="`/datasources/${source.id}/tags`"
      >
        <span class="dash-source-name">
          <span class="dash-dot" :class="`is-${sourceTone(source)}`" aria-hidden="true" />
          <SourceTypeIcon :type="source.type" />
          <span class="dash-ellipsis" :data-hint="`${source.name} · ${typeLabel(source.type)}`">{{ source.name }}</span>
        </span>
        <span><span class="badge" :class="badgeClass(source.state)">{{ source.state }}</span></span>
        <ThroughputSparkline :values="source.valuesPerSecSeries ?? []" :current="source.valuesPerSec" />
        <span
          class="dash-num"
          :class="{ 'is-warn': clockOff(source) }"
          :data-hint="clockOff(source) ? clockText(source.clockOffsetMs) : undefined"
        >{{ clockCell(source) }}</span>
      </router-link>
    </section>
    </div>
    </div>

    <WaitOverlay
      :visible="busy"
      :title="overlayTitle"
      :message="overlayMessage"
      :error="overlayError"
      :cancellable="true"
      @cancel="cancelCurrent"
    />

    <TagChartOverlay
      v-if="showRateChart && rateChart && dashboard"
      :data-source-id="dashboard.systemLoadSourceId!"
      :tag-id="dashboard.valueCountPerSecTagId!"
      tag-name="ArchiveValueCountPerSec"
      :source-name="dashboard.systemLoadSourceName ?? ''"
      @close="showRateChart = false"
    />

    <TagChartOverlay
      v-if="showLoadChart && loadChart && dashboard"
      :data-source-id="dashboard.systemLoadSourceId!"
      :tag-id="dashboard.systemLoadTagId!"
      tag-name="SystemLoadPercent"
      :source-name="dashboard.systemLoadSourceName ?? ''"
      @close="showLoadChart = false"
    />

    <TagChartOverlay
      v-if="showValueCountChart && valueCountChart && dashboard"
      :data-source-id="dashboard.systemLoadSourceId!"
      :tag-id="dashboard.valueCountSumTagId!"
      tag-name="ArchiveValueCountSumMio"
      :source-name="dashboard.systemLoadSourceName ?? ''"
      @close="showValueCountChart = false"
    />

    <TagChartOverlay
      v-if="showDatabaseChart && databaseChart && dashboard"
      :data-source-id="dashboard.systemLoadSourceId!"
      :tag-id="dashboard.databaseDiskSizeTagId!"
      tag-name="DatabaseDiskSizeGb"
      :source-name="dashboard.systemLoadSourceName ?? ''"
      @close="showDatabaseChart = false"
    />
</template>

<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, ref } from "vue";
import SourceTypeIcon from "../components/SourceTypeIcon.vue";
import TagChartOverlay from "../components/TagChartOverlay.vue";
import ThroughputSparkline from "../components/ThroughputSparkline.vue";
import TrendSparkline from "../components/TrendSparkline.vue";
import WaitOverlay from "../components/WaitOverlay.vue";
import { createPollSession, getDashboard, isAbortError } from "../api";
import { formatDateTime, formatInteger, formatNumber } from "../format";
import { badgeClass, clockOff, clockShort, clockText, typeLabel } from "../sourceState";
import type { Dashboard, DataSourceListItem } from "../types";

type Tone = "ok" | "warn" | "danger" | "idle";

// The small chart mark beside the label of a tile that opens a chart
const ChartGlyph = () => h("svg", { class: "dash-chart-glyph", viewBox: "0 0 24 24", "aria-hidden": "true" }, [
  h("path", { d: "M4 19V5" }),
  h("path", { d: "M4 19h16" }),
  h("path", { d: "M7 14l3.5-4 3 2.5L20 6" })
]);

const dashboard = ref<Dashboard | null>(null);
const busy = ref(true);
const overlayError = ref("");
const overlayTitle = ref("Loading");
const overlayMessage = ref("Loading dashboard …");
const pollError = ref("");
const showLoadChart = ref(false);
const showDatabaseChart = ref(false);
const showValueCountChart = ref(false);
const showRateChart = ref(false);
const lastLoadedAt = ref<Date | null>(null);
const statusText = computed(() => {
  if (!dashboard.value?.statsUpdatedAt) {
    return "Archive statistics not available yet";
  }
  return `Statistics: ${formatDateTime(new Date(dashboard.value.statsUpdatedAt))}`;
});

const rateChart = computed(() => dashboard.value?.valueCountPerSecTagId != null && dashboard.value.systemLoadSourceId != null);
const loadChart = computed(() => dashboard.value?.systemLoadTagId != null && dashboard.value.systemLoadSourceId != null);
const databaseChart = computed(() => dashboard.value?.databaseDiskSizeTagId != null && dashboard.value.systemLoadSourceId != null);
const valueCountChart = computed(() => dashboard.value?.valueCountSumTagId != null && dashboard.value.systemLoadSourceId != null);

const loadTone = computed<Tone>(() => {
  const percent = dashboard.value?.systemLoadPercent;
  if (percent == null) return "idle";
  if (percent >= 90) return "danger";
  if (percent >= 70) return "warn";
  return "ok";
});

const loadWidth = computed(() => Math.min(Math.max(dashboard.value?.systemLoadPercent ?? 0, 0), 100));

const growthText = computed(() => {
  const growth = dashboard.value?.databaseGrowthGb7d;
  if (growth == null) {
    return "Growth after a week of data";
  }
  const sign = growth > 0 ? "+" : growth < 0 ? "−" : "±";
  return `${sign}${formatNumber(Math.abs(growth), { maximumFractionDigits: 2 })} GB in 7 days`;
});

function isConnected(source: DataSourceListItem): boolean {
  return badgeClass(source.state) === "connected";
}

function isFailed(source: DataSourceListItem): boolean {
  return badgeClass(source.state) === "error" || source.opcUaCertificateRejected;
}

function sourceTone(source: DataSourceListItem): Tone {
  if (isFailed(source)) return "danger";
  if (!source.isActive || badgeClass(source.state) === "inactive") return "idle";
  if (!isConnected(source) || clockOff(source)) return "warn";
  return "ok";
}

const TONE_ORDER: Record<Tone, number> = { danger: 0, warn: 1, ok: 2, idle: 3 };

// Sources that need a look come first, the inactive ones last
const sortedSources = computed(() => {
  return [...(dashboard.value?.sources ?? [])].sort((a, b) =>
    TONE_ORDER[sourceTone(a)] - TONE_ORDER[sourceTone(b)] || a.name.localeCompare(b.name));
});

const toneCounts = computed(() => {
  const counts: Record<Tone, number> = { ok: 0, warn: 0, danger: 0, idle: 0 };
  for (const source of dashboard.value?.sources ?? []) {
    counts[sourceTone(source)]++;
  }
  return counts;
});

const stackParts = computed(() => {
  const counts = toneCounts.value;
  return (["ok", "warn", "danger", "idle"] as Tone[])
    .filter((tone) => counts[tone] > 0)
    .map((tone) => ({ key: tone, tone, count: counts[tone] }));
});

const legendParts = computed(() => {
  const counts = toneCounts.value;
  const parts = [
    { key: "ok", tone: "ok" as Tone, label: "connected", count: counts.ok },
    { key: "warn", tone: "warn" as Tone, label: "with warning", count: counts.warn },
    { key: "danger", tone: "danger" as Tone, label: "with error", count: counts.danger },
    { key: "idle", tone: "idle" as Tone, label: "inactive", count: counts.idle }
  ];
  // Connected and error are always named, warning and inactive only when there are some
  return parts.filter((part) => part.count > 0 || part.key === "ok" || part.key === "danger");
});

const stackLabel = computed(() => legendParts.value.map((part) => `${part.count} ${part.label}`).join(", "));

const health = computed<{ tone: Tone; text: string }>(() => {
  if (pollError.value) {
    const at = lastLoadedAt.value;
    return { tone: "danger", text: at ? `No connection · state of ${formatDateTime(at)}` : "No connection" };
  }
  const counts = toneCounts.value;
  if (counts.danger > 0) {
    return { tone: "danger", text: counts.danger === 1 ? "1 source with error" : `${counts.danger} sources with error` };
  }
  const pending = dashboard.value?.pendingAlarms ?? 0;
  if (pending > 0) {
    return { tone: "warn", text: pending === 1 ? "1 pending alarm" : `${pending} pending alarms` };
  }
  if (counts.warn > 0) {
    return { tone: "warn", text: counts.warn === 1 ? "1 source with warning" : `${counts.warn} sources with warning` };
  }
  return { tone: "ok", text: "All sources running" };
});

function clockCell(source: DataSourceListItem): string {
  if (source.clockOffsetMs == null) {
    return "–";
  }
  return Math.abs(source.clockOffsetMs) < 1000
    ? `${formatInteger(source.clockOffsetMs)} ms`
    : clockShort(source.clockOffsetMs);
}

const poll = createPollSession();
let timer: number | undefined;
let loading = false;

function formatCount(value: number | null | undefined): string {
  return value == null ? "–" : formatInteger(value);
}

function formatRate(value: number | null | undefined): string {
  return value == null ? "–" : formatNumber(value);
}

function formatMio(value: number | null | undefined): string {
  return value == null ? "–" : formatNumber(value, { maximumFractionDigits: 2 });
}

function formatGb(value: number | null | undefined): string {
  return value == null ? "–" : formatNumber(value, { maximumFractionDigits: 2 });
}

function formatPercent(value: number | null | undefined): string {
  return value == null ? "–" : `${formatNumber(value, { maximumFractionDigits: 1 })} %`;
}

function cancelCurrent() {
  poll.abort();
  loading = false;
  busy.value = false;
  overlayError.value = "The request was cancelled.";
}

async function load(initial: boolean) {
  const session = poll.begin();
  loading = true;
  if (initial || !dashboard.value) {
    busy.value = true;
    overlayError.value = "";
    overlayTitle.value = "Loading";
    overlayMessage.value = "Loading dashboard …";
  }

  try {
    const data = await getDashboard(session.signal);
    if (!session.isCurrent()) {
      return;
    }
    dashboard.value = data;
    lastLoadedAt.value = new Date();
    overlayError.value = "";
    pollError.value = "";
    busy.value = false;
  } catch (error) {
    if (!session.isCurrent() || isAbortError(error)) {
      return;
    }
    const message = error instanceof Error ? error.message : "Unknown error";
    pollError.value = message;
    if (!dashboard.value) {
      overlayError.value = message;
      overlayTitle.value = "Error";
      busy.value = true;
    } else {
      busy.value = false;
    }
  } finally {
    if (session.isCurrent()) {
      loading = false;
    }
  }
}

onMounted(() => {
  void load(true);
  timer = window.setInterval(() => {
    // A slow answer (a busy Raspberry Pi) is waited for instead of being cancelled by the next poll
    if (!loading) {
      void load(false);
    }
  }, 2000);
});

onUnmounted(() => {
  if (timer !== undefined) {
    window.clearInterval(timer);
  }
  poll.abort();
});
</script>
