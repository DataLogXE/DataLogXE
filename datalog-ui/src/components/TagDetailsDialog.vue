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
  <div class="overlay chart-dialog-overlay" @click.self="requestClose">
    <form
      class="overlay-card chart-dialog tag-details-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="tag-details-title"
      @submit.prevent="save"
    >
      <div class="chart-stats-head">
        <h2 id="tag-details-title" class="chart-stats-title">Details</h2>
        <button class="panel-close" type="button" data-hint="Close" aria-label="Close" @click="requestClose">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M7 7l10 10M17 7L7 17" />
          </svg>
        </button>
        <p class="hint chart-stats-tag" :data-hint="subtitle">{{ subtitle }}</p>
      </div>

      <p v-if="loading" class="hint chart-stats-status">
        <span class="spinner spinner-sm" aria-hidden="true"></span>
        Loading …
      </p>
      <template v-else-if="rows.length">
        <!-- The name is in the subtitle; with several tags it shows here whether they share one -->
        <dl class="tag-details-list">
          <template v-if="rows.length > 1">
            <dt>Name</dt>
            <dd :data-hint="nameText">{{ nameText }}</dd>
          </template>
          <dt>Display name</dt>
          <dd :class="{ 'is-empty': !displayNameText }" :data-hint="displayNameText">{{ displayNameText || "–" }}</dd>
          <dt>Address</dt>
          <dd :data-hint="addressText">{{ addressText }}</dd>
          <template v-if="hasComment">
            <dt>Comment</dt>
            <dd class="tag-details-comment" :data-hint="commentText">{{ commentText }}</dd>
          </template>
        </dl>

        <!-- Settings and Info in two tabs; both panels share one cell, so the dialog keeps its height -->
        <div class="tag-details-tabs" role="tablist" aria-label="Data tag details">
          <button
            v-for="tab in detailTabs"
            :id="`tag-details-tab-${tab.id}`"
            :key="tab.id"
            type="button"
            role="tab"
            class="tag-details-tab"
            :class="{ active: detailTab === tab.id }"
            :aria-selected="detailTab === tab.id"
            :aria-controls="`tag-details-panel-${tab.id}`"
            @click="detailTab = tab.id"
          >
            {{ tab.label }}
          </button>
        </div>
        <div class="tag-details-panels">
          <div
            id="tag-details-panel-settings"
            class="tag-details-panel"
            :class="{ 'is-hidden': detailTab !== 'settings' }"
            role="tabpanel"
            aria-labelledby="tag-details-tab-settings"
          >
            <dl class="tag-details-list">
              <dt><label for="tag-details-active">Active</label></dt>
              <dd class="tag-details-edit">
                <input
                  id="tag-details-active"
                  type="checkbox"
                  :checked="activeShown === true"
                  :indeterminate="activeShown === null"
                  :disabled="readOnly || saving"
                  :data-hint="activeShown === null ? 'Active in some of the data tags. Click to set it for all.' : undefined"
                  @change="onActiveChange"
                />
                <span v-if="activeShown === null" class="hint">{{ MIXED_TEXT }}</span>
                <button
                  v-if="activeEdit !== null"
                  class="tag-details-reset"
                  type="button"
                  data-hint="Back to the stored value"
                  aria-label="Reset Active"
                  @click="activeEdit = null"
                >
                  <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3.5 6.5h6a3 3 0 0 1 0 6H6M6 3.5l-3 3 3 3" /></svg>
                </button>
              </dd>

              <dt><label for="tag-details-cycle">Cycle in ms</label></dt>
              <dd class="tag-details-edit">
                <select id="tag-details-cycle" v-model="cycleModel" :disabled="readOnly || saving" :class="{ edited: cycleEdit !== null }">
                  <option v-if="cycleModel === null" :value="null" disabled>{{ MIXED_TEXT }}</option>
                  <option v-for="cycle in cycleOptions" :key="cycle" :value="cycle">{{ cycle }}</option>
                </select>
                <button
                  v-if="cycleEdit !== null"
                  class="tag-details-reset"
                  type="button"
                  data-hint="Back to the stored value"
                  aria-label="Reset Cycle"
                  @click="cycleEdit = null"
                >
                  <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3.5 6.5h6a3 3 0 0 1 0 6H6M6 3.5l-3 3 3 3" /></svg>
                </button>
              </dd>
            </dl>
          </div>
          <div
            id="tag-details-panel-info"
            class="tag-details-panel"
            :class="{ 'is-hidden': detailTab !== 'info' }"
            role="tabpanel"
            aria-labelledby="tag-details-tab-info"
          >
            <dl class="tag-details-list">
              <dt>Data type</dt>
              <dd>{{ typeText }}</dd>
              <dt>Created</dt>
              <dd>{{ createdText }}</dd>
              <dt>ID</dt>
              <dd :data-hint="idText">{{ idText }}</dd>
              <dt>GUID</dt>
              <dd class="tag-details-guid" :data-hint="guidText">{{ guidText }}</dd>
            </dl>
          </div>
        </div>
        <p v-if="sysOnly" class="hint">System data tags cannot be changed.</p>
        <p v-else-if="missingCount > 0" class="hint">
          {{ missingCount === 1 ? "1 data tag was" : `${missingCount} data tags were` }} deleted meanwhile.
        </p>
      </template>
      <p v-if="error" class="error-text">{{ error }}</p>

      <div class="actions chart-dialog-actions">
        <button class="btn secondary" type="button" :disabled="saving" @click="requestClose">
          {{ readOnly ? "Close" : "Cancel" }}
        </button>
        <button v-if="!readOnly" class="btn" type="submit" :disabled="!dirty || saving">
          {{ saving ? "Saving …" : "Save" }}
        </button>
      </div>
    </form>

    <ConfirmDialog
      v-if="confirmDiscard"
      title="Discard changes"
      message="The changes in the details are not saved."
      confirm-label="Discard"
      :busy="false"
      error=""
      @confirm="emit('close')"
      @close="confirmDiscard = false"
    />
  </div>
</template>

<script setup lang="ts">
// The details of one or more data tags. A property that differs between the tags shows <>; Active and Cycle set to a
// value are written to all of them, an untouched field stays as it is in every tag.
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import ConfirmDialog from "./ConfirmDialog.vue";
import { getDataTagDetails, isAbortError, updateDataTags } from "../api";
import { formatDateTime, formatInteger } from "../format";
import { notify } from "../notify";
import { isAdmin } from "../session";
import { editFor, mergedText, mergeValue, MIXED_TEXT, shownValue } from "../tagDetailsMerge";
import type { DataTagDetails, DataTagListItem } from "../types";

const props = defineProps<{
  dataSourceId: number;
  tagIds: number[];
}>();

const emit = defineEmits<{
  close: [];
  updated: [rows: DataTagListItem[]];
}>();

const rows = ref<DataTagDetails[]>([]);
const cycles = ref<number[]>([]);
const loading = ref(true);
const saving = ref(false);
const error = ref("");
const activeEdit = ref<boolean | null>(null);
const cycleEdit = ref<number | null>(null);
const confirmDiscard = ref(false);
const detailTabs = [
  { id: "settings", label: "Settings" },
  { id: "info", label: "Info" }
] as const;
const detailTab = ref<(typeof detailTabs)[number]["id"]>("settings");
let controller: AbortController | null = null;

const writableIds = computed(() => rows.value.filter((row) => !row.isSys).map((row) => row.id));
const sysOnly = computed(() => rows.value.length > 0 && writableIds.value.length === 0);
const readOnly = computed(() => !isAdmin.value || writableIds.value.length === 0);
const missingCount = computed(() => (loading.value ? 0 : props.tagIds.length - rows.value.length));

const subtitle = computed(() => {
  if (rows.value.length === 1) {
    return rows.value[0].name;
  }
  return `${formatInteger(rows.value.length || props.tagIds.length)} data tags`;
});

const nameText = computed(() => mergedText(mergeValue(rows.value, (row) => row.name)));
const displayNameText = computed(() => mergedText(mergeValue(rows.value, (row) => row.displayName)));
const addressText = computed(() => mergedText(mergeValue(rows.value, (row) => row.address)));
// Shown when one of the tags has a comment (S7: from the PLC, in the language of the data source)
const hasComment = computed(() => rows.value.some((row) => !!row.comment));
const commentText = computed(() => mergedText(mergeValue(rows.value, (row) => row.comment ?? "")));
const typeText = computed(() => mergedText(mergeValue(rows.value, (row) => row.type), typeLabel));
const idText = computed(() => mergedText(mergeValue(rows.value, (row) => row.id)));
const guidText = computed(() => mergedText(mergeValue(rows.value, (row) => row.guid)));
const createdText = computed(() => mergedText(mergeValue(rows.value, (row) => createdLabel(row.createdAt))));

const activeMerged = computed(() => mergeValue(rows.value, (row) => row.isActive));
const cycleMerged = computed(() => mergeValue(rows.value, (row) => row.cycle));
const activeShown = computed(() => shownValue(activeMerged.value, activeEdit.value));

const cycleModel = computed<number | null>({
  get: () => shownValue(cycleMerged.value, cycleEdit.value),
  set: (value) => {
    cycleEdit.value = value === null ? null : editFor(cycleMerged.value, value);
  }
});

// The cycles of the source, plus a stored cycle that is no longer in the list, so the select can show it
const cycleOptions = computed(() => {
  const options = new Set(cycles.value);
  for (const row of rows.value) {
    options.add(row.cycle);
  }
  return [...options].sort((a, b) => a - b);
});

const dirty = computed(() => activeEdit.value !== null || cycleEdit.value !== null);

function typeLabel(type: string) {
  return type === "Bool" || type === "Number" ? type : "–";
}

function createdLabel(createdAt: string | null) {
  if (!createdAt) {
    return "–";
  }
  const date = new Date(createdAt);
  return Number.isNaN(date.getTime()) ? "–" : formatDateTime(date);
}

function onActiveChange(event: Event) {
  activeEdit.value = editFor(activeMerged.value, (event.target as HTMLInputElement).checked);
}

function requestClose() {
  if (saving.value) {
    return;
  }
  if (dirty.value) {
    confirmDiscard.value = true;
    return;
  }
  emit("close");
}

// On the window, since the focus leaves the form when a reset button disappears; the question to discard answers
// its own Escape first
function onKeyDown(event: KeyboardEvent) {
  if (event.key !== "Escape" || event.defaultPrevented) {
    return;
  }
  event.preventDefault();
  if (confirmDiscard.value) {
    confirmDiscard.value = false;
  } else {
    requestClose();
  }
}

async function load() {
  controller?.abort();
  controller = new AbortController();
  loading.value = true;
  error.value = "";
  try {
    const data = await getDataTagDetails(props.dataSourceId, props.tagIds, controller.signal);
    rows.value = data.items;
    cycles.value = data.cycles;
    if (!data.items.length) {
      error.value = "The data tags were not found.";
    }
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    error.value = err instanceof Error ? err.message : "The details could not be loaded.";
  } finally {
    loading.value = false;
  }
}

async function save() {
  if (!dirty.value || readOnly.value || saving.value) {
    return;
  }
  controller?.abort();
  controller = new AbortController();
  saving.value = true;
  error.value = "";
  try {
    const result = await updateDataTags(
      props.dataSourceId,
      writableIds.value,
      {
        isActive: activeEdit.value ?? undefined,
        cycle: cycleEdit.value ?? undefined
      },
      controller.signal
    );
    emit("updated", result.updated);
    if (result.notActivated) {
      notify("warning", "Not all activated",
        `${formatInteger(result.notActivated)} of ${formatInteger(writableIds.value.length)} data tags stay inactive: strings and system types cannot be logged.`);
    }
    emit("close");
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    error.value = err instanceof Error ? err.message : "The changes could not be saved.";
  } finally {
    saving.value = false;
  }
}

onMounted(() => {
  window.addEventListener("keydown", onKeyDown);
  void load();
});

onBeforeUnmount(() => {
  window.removeEventListener("keydown", onKeyDown);
  controller?.abort();
});
</script>
