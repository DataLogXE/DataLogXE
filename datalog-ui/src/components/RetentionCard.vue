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
  <section class="form-card settings-card">
    <h2>{{ title }}</h2>
    <p v-if="hint" class="hint retention-hint">{{ hint }}</p>
    <p class="settings-row">
      <span>Active</span>
      <strong>{{ activeLine }}</strong>
    </p>
    <div class="retention-set">
      <select v-model="presetId" class="retention-select" :disabled="locked" aria-label="Set retention">
        <option v-for="preset in retentionPresets" :key="preset.id" :value="preset.id">
          {{ preset.label }}
        </option>
      </select>
      <button type="button" class="btn" :disabled="!canApply" @click="apply">
        Apply
      </button>
    </div>
    <div v-if="presetId === 'custom'" class="retention-custom">
      <input
        v-model.number="customAmount"
        type="number"
        min="1"
        step="1"
        inputmode="numeric"
        aria-label="Amount"
        :disabled="locked"
      />
      <select v-model="customUnit" aria-label="Unit" :disabled="locked">
        <option value="hours">Hours</option>
        <option value="days">Days</option>
        <option value="weeks">Weeks</option>
      </select>
    </div>
    <p v-if="pendingText" class="settings-pending">{{ pendingText }}</p>
    <p v-if="error" class="error-text">{{ error }}</p>
  </section>
</template>

<script setup lang="ts">
import { computed, ref, watch } from "vue";
import { formatInteger } from "../format";

const props = defineProps<{
  title: string;
  hint?: string;
  seconds: number | null;
  saving: boolean;
  error: string;
}>();

const emit = defineEmits<{
  apply: [seconds: number];
}>();

const HOUR_SECONDS = 3600;
const DAY_SECONDS = 24 * HOUR_SECONDS;
const WEEK_SECONDS = 7 * DAY_SECONDS;
const RETENTION_MAX_SECONDS = 10 * 365 * DAY_SECONDS;

const retentionPresets = [
  { id: "forever", label: "Forever", seconds: 0 },
  { id: "1d", label: "1 day", seconds: DAY_SECONDS },
  { id: "7d", label: "7 days", seconds: 7 * DAY_SECONDS },
  { id: "2w", label: "2 weeks", seconds: 2 * WEEK_SECONDS },
  { id: "4w", label: "4 weeks", seconds: 4 * WEEK_SECONDS },
  { id: "30d", label: "30 days", seconds: 30 * DAY_SECONDS },
  { id: "90d", label: "90 days", seconds: 90 * DAY_SECONDS },
  { id: "180d", label: "180 days", seconds: 180 * DAY_SECONDS },
  { id: "1y", label: "1 year", seconds: 365 * DAY_SECONDS },
  { id: "custom", label: "Custom", seconds: null }
] as const;

type RetentionUnit = "hours" | "days" | "weeks";

const presetId = ref<string>("4w");
const customAmount = ref(14);
const customUnit = ref<RetentionUnit>("days");

const locked = computed(() => props.saving || props.seconds == null);

const draftSeconds = computed(() => retentionSecondsFromDraft());

const canApply = computed(() => {
  const next = draftSeconds.value;
  const active = props.seconds;
  return !locked.value && next != null && active != null && next !== active;
});

const activeLine = computed(() => {
  const title = retentionTitle(props.seconds);
  const exact = retentionSecondsText(props.seconds);
  return exact ? `${title} · ${exact}` : title;
});

const pendingText = computed(() => {
  const next = draftSeconds.value;
  const active = props.seconds;
  if (active == null) {
    return "";
  }
  if (next == null) {
    return "At least 1 hour.";
  }
  if (next === active) {
    return "";
  }
  const preset = retentionPresets.find((item) => item.id === presetId.value);
  const label = preset?.seconds != null ? preset.label : retentionTitle(next);
  return `${label} · ${retentionSecondsText(next)}`;
});

function retentionTitle(seconds: number | null | undefined): string {
  if (seconds == null) {
    return "Unavailable";
  }
  if (seconds === 0) {
    return "Forever";
  }
  if (seconds % WEEK_SECONDS === 0) {
    const weeks = seconds / WEEK_SECONDS;
    return weeks === 1 ? "1 week" : `${formatInteger(weeks)} weeks`;
  }
  if (seconds % DAY_SECONDS === 0) {
    const days = seconds / DAY_SECONDS;
    return days === 1 ? "1 day" : `${formatInteger(days)} days`;
  }
  if (seconds % HOUR_SECONDS === 0) {
    const hours = seconds / HOUR_SECONDS;
    return hours === 1 ? "1 hour" : `${formatInteger(hours)} hours`;
  }
  const days = Math.floor(seconds / DAY_SECONDS);
  const hours = Math.floor((seconds % DAY_SECONDS) / HOUR_SECONDS);
  if (days > 0 && hours > 0) {
    return `${formatInteger(days)} days ${formatInteger(hours)} hours`;
  }
  return `${formatInteger(seconds)} seconds`;
}

function retentionSecondsText(seconds: number | null | undefined): string {
  if (seconds == null || seconds === 0) {
    return "";
  }
  return `${formatInteger(seconds)} s`;
}

function retentionSecondsFromDraft(): number | null {
  const preset = retentionPresets.find((item) => item.id === presetId.value);
  if (!preset) {
    return null;
  }
  if (preset.seconds != null) {
    return preset.seconds;
  }
  const amount = customAmount.value;
  if (!Number.isInteger(amount) || amount < 1) {
    return null;
  }
  const factor = customUnit.value === "hours"
    ? HOUR_SECONDS
    : customUnit.value === "weeks"
      ? WEEK_SECONDS
      : DAY_SECONDS;
  const seconds = amount * factor;
  if (seconds < HOUR_SECONDS || seconds > RETENTION_MAX_SECONDS) {
    return null;
  }
  return seconds;
}

function syncDraft(seconds: number | null) {
  if (seconds == null) {
    presetId.value = "custom";
    return;
  }
  const match = retentionPresets.find((item) => item.seconds === seconds);
  if (match) {
    presetId.value = match.id;
    return;
  }
  presetId.value = "custom";
  if (seconds % WEEK_SECONDS === 0) {
    customUnit.value = "weeks";
    customAmount.value = seconds / WEEK_SECONDS;
    return;
  }
  if (seconds % DAY_SECONDS === 0) {
    customUnit.value = "days";
    customAmount.value = seconds / DAY_SECONDS;
    return;
  }
  if (seconds % HOUR_SECONDS === 0) {
    customUnit.value = "hours";
    customAmount.value = seconds / HOUR_SECONDS;
  }
}

function apply() {
  const seconds = draftSeconds.value;
  if (seconds == null || !canApply.value) {
    return;
  }
  emit("apply", seconds);
}

// The confirmed value from the server resets the draft, also after a failed or partial change.
watch(() => props.seconds, (value) => syncDraft(value), { immediate: true });
</script>

<style scoped>
.retention-hint {
  margin: 0.6rem 0 0;
}
</style>
