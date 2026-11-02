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
    v-if="item"
    ref="detailRef"
    class="events-detail"
    :class="{ 'source-offline': sourceOffline || updatesStale, sized: detailHeight != null }"
    :style="detailHeight != null ? { '--events-detail-height': `${detailHeight}px` } : undefined"
  >
    <div
      class="events-detail-resize"
      :class="{ dragging: resizing }"
      role="separator"
      aria-orientation="horizontal"
      aria-label="Height of the details"
      tabindex="0"
      data-hint="Drag to change the height (double click: fit to the content)"
      @pointerdown="onResizeDown"
      @pointermove="onResizeMove"
      @pointerup="onResizeEnd"
      @pointercancel="onResizeEnd"
      @dblclick="setDetailHeight(null)"
      @keydown="onResizeKey"
    />
    <button
      type="button"
      class="panel-close"
      data-hint="Close"
      aria-label="Close details"
      @click="emit('close')"
    >
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M7 7l10 10M17 7L7 17" />
      </svg>
    </button>
    <div class="events-detail-tabs" role="tablist" aria-label="Alarm detail">
      <button
        v-for="tab in detailTabs"
        :id="`events-tab-${tab.id}`"
        :key="tab.id"
        type="button"
        role="tab"
        class="events-detail-tab"
        :class="{ active: detailTab === tab.id }"
        :aria-selected="detailTab === tab.id"
        :aria-controls="`events-panel-${tab.id}`"
        @click="detailTab = tab.id"
      >
        {{ tab.label }}
      </button>
    </div>
    <div class="events-detail-scroll">
      <p v-if="updatesStale" class="events-source-offline" role="status">
        Updates stopped. These alarms may no longer be current.
      </p>
      <p v-else-if="sourceOffline" class="events-source-offline" role="status">
        Source disconnected. This alarm may no longer be current.
      </p>
      <div v-show="detailTab === 'message'" id="events-panel-message" role="tabpanel" aria-labelledby="events-tab-message">
        <section class="events-detail-message-block" aria-label="Message">
          <pre class="events-detail-message">{{ item.message }}</pre>
        </section>
        <slot name="message-extra" />
      </div>
      <section
        v-show="detailTab === 'details'"
        id="events-panel-details"
        class="events-detail-fields"
        role="tabpanel"
        aria-labelledby="events-tab-details"
      >
        <dl>
          <div><dt>Time</dt><dd class="events-detail-time">{{ formatDateTimeMs(new Date(item.time)) }}</dd></div>
          <div><dt>State</dt><dd>{{ formatAlarmState(item.alarmState, limit) }}</dd></div>
          <div><dt>Severity</dt><dd>{{ item.severity ?? "—" }}</dd></div>
          <div><dt>Duration</dt><dd>{{ formatDurationMs(item.durationMs) }}</dd></div>
          <div>
            <dt>Active since</dt>
            <dd class="events-detail-time">{{ item.activeSince ? formatDateTimeMs(new Date(item.activeSince)) : "—" }}</dd>
          </div>
          <div><dt>Active</dt><dd>{{ formatYesNo(item.active) }}</dd></div>
          <div><dt>Acked</dt><dd>{{ formatYesNo(item.acked) }}</dd></div>
          <div><dt>Retain</dt><dd>{{ formatYesNo(item.retain) }}</dd></div>
          <div><dt>Class</dt><dd>{{ formatClassDetail(item) }}</dd></div>
          <div>
            <dt>Type</dt>
            <dd :data-hint="item.eventType || undefined">{{ item.eventTypeName || item.eventType || "—" }}</dd>
          </div>
          <div><dt>Source</dt><dd>{{ sourceLabel }}</dd></div>
          <div v-if="processProps.condition"><dt>Condition</dt><dd>{{ processProps.condition }}</dd></div>
          <div v-if="processProps.opcSource"><dt>OPC source</dt><dd>{{ processProps.opcSource }}</dd></div>
          <div v-if="processProps.branch"><dt>Branch</dt><dd>{{ processProps.branch }}</dd></div>
          <div v-if="processProps.location"><dt>Location</dt><dd>{{ processProps.location }}</dd></div>
          <div v-for="field in processProps.s7Fields" :key="field.label">
            <dt>{{ field.label }}</dt><dd>{{ field.value }}</dd>
          </div>
        </dl>
      </section>
      <section
        v-if="statistics && detailTab === 'statistics'"
        id="events-panel-statistics"
        role="tabpanel"
        aria-labelledby="events-tab-statistics"
      >
        <AlarmStatistics :item="item" />
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { DETAIL_HEIGHT_MIN, formatClassDetail, readDetailHeight, readProcessProps } from "../alarmCommon";
import AlarmStatistics from "./AlarmStatistics.vue";
import { formatAlarmState, formatDateTimeMs, formatDurationMs, formatYesNo, limitStateFromProps } from "../format";
import type { EventLogItem } from "../types";

// The detail panel under the table of an alarm page. Its height is kept in the browser under the key of the page.
const props = defineProps<{
  /** The key in the browser that keeps the height of this panel; every page has its own. */
  storageKey: string;
  /** The alarm shown; the panel is empty (not drawn) without one. */
  item: EventLogItem | null;
  sourceLabel: string;
  sourceOffline?: boolean;
  updatesStale?: boolean;
  /** Shows the tab Statistics (the history; the pending alarms leave it out). */
  statistics?: boolean;
}>();

const emit = defineEmits<{
  close: [];
}>();

// The key of the time before every page had its own height: read while a page has none of its own yet
const LEGACY_HEIGHT_KEY = "alarmDetailHeight";
// The table keeps this much height above the panel
const DETAIL_TABLE_MIN = 140;
const DETAIL_HEIGHT_STEP = 24;

type DetailTab = "message" | "details" | "statistics";
const allDetailTabs: { id: DetailTab; label: string }[] = [
  { id: "message", label: "Message" },
  // Mounted only while shown: it counts when it is opened
  { id: "statistics", label: "Statistics" },
  { id: "details", label: "Details" }
];
const detailTabs = computed(() => allDetailTabs.filter((tab) => tab.id !== "statistics" || props.statistics));
const detailTab = ref<DetailTab>("message");
const detailRef = ref<HTMLElement | null>(null);
const resizing = ref(false);
const detailHeight = ref<number | null>(readDetailHeight(props.storageKey, LEGACY_HEIGHT_KEY));

const processProps = computed(() => readProcessProps(props.item?.props));
const limit = computed(() => limitStateFromProps(props.item?.props));

function clampDetailHeight(value: number): number {
  const available = detailRef.value?.parentElement?.clientHeight ?? Number.POSITIVE_INFINITY;
  return Math.round(Math.max(DETAIL_HEIGHT_MIN, Math.min(value, available - DETAIL_TABLE_MIN)));
}

function setDetailHeight(value: number | null) {
  detailHeight.value = value == null ? null : clampDetailHeight(value);
  try {
    // Empty, not removed: a missing value would bring back the height of the legacy key
    window.localStorage.setItem(props.storageKey, detailHeight.value == null ? "" : String(detailHeight.value));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

// The panel ends at the bottom of the page, so the pointer gives the height
function onResizeDown(event: PointerEvent) {
  if (event.button !== 0) {
    return;
  }
  (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  resizing.value = true;
  event.preventDefault();
}

function onResizeMove(event: PointerEvent) {
  const detail = detailRef.value;
  if (!resizing.value || !detail) {
    return;
  }
  detailHeight.value = clampDetailHeight(detail.getBoundingClientRect().bottom - event.clientY);
}

function onResizeEnd() {
  if (resizing.value) {
    resizing.value = false;
    setDetailHeight(detailHeight.value);
  }
}

function onResizeKey(event: KeyboardEvent) {
  const step = event.key === "ArrowUp" ? DETAIL_HEIGHT_STEP : event.key === "ArrowDown" ? -DETAIL_HEIGHT_STEP : 0;
  if (step !== 0) {
    event.preventDefault();
    const current = detailHeight.value ?? Math.round(detailRef.value?.getBoundingClientRect().height ?? DETAIL_HEIGHT_MIN);
    setDetailHeight(current + step);
  }
}
</script>
