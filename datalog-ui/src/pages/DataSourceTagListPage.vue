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
    <div class="tag-page">
      <div class="page-header">
        <h1>
          Data tags<template v-if="sourceName">
            · <SourceTypeIcon v-if="sourceType" class="tag-page-type-icon" :type="sourceType" />{{ sourceName }}</template
          >
        </h1>
        <div class="page-header-actions">
          <TagJobButton
            v-if="isOpcUa && isAdmin"
            label="Import OPC-UA XML"
            :state="xmlImportActive ? 'running' : xmlImportButtonShowsError ? 'error' : 'idle'"
            :error="xmlImportError"
            @click="openXmlImport"
          />
          <TagJobButton
            v-if="canBrowse && isAdmin"
            :label="browseTitle"
            :state="browseHeaderActive ? 'running' : browseButtonShowsError ? 'error' : 'idle'"
            :error="browseError"
            @click="openBrowse"
          />
          <button
            v-if="isAdmin"
            class="btn secondary"
            type="button"
            :disabled="!selected.size || busy"
            data-hint="Add the selected data tags to a chart"
            @click="openAddToChart"
          >
            Add to chart
          </button>
          <router-link class="btn secondary" :to="`/datasources/${route.params.id}/log`">Log</router-link>
          <router-link class="btn secondary" to="/datasources">Back</router-link>
        </div>
      </div>

      <div class="tag-bar">
        <div class="tag-filter">
          <div class="tag-filter-field">
            <input
              id="tag-filter"
              v-model="filterText"
              type="text"
              placeholder="ID, name, display name, address, or comment"
              data-hint="Filters ID, name, display name, address, and comment. Wildcards * and ? are supported."
              aria-label="Filter by ID, name, display name, address, or comment"
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
          <button
            class="tag-switch"
            type="button"
            :class="{ on: activeOnly }"
            :aria-pressed="activeOnly"
            aria-label="Show only active data tags"
            data-hint="Show only active data tags"
            @click="toggleActiveOnly"
          >
            <span class="tag-switch-knob" aria-hidden="true">
              <svg viewBox="0 0 16 16">
                <circle cx="8" cy="8" r="3.2" />
              </svg>
            </span>
          </button>
        </div>

        <p class="tag-status" :data-hint="countTooltip">
          {{ countLabel }}
          <span v-if="selected.size"> · {{ selected.size }} selected</span>
        </p>
      </div>

      <p v-if="error" class="error-text">{{ error }}</p>

      <VirtualTagTable
        ref="tagTableRef"
        :key="filterGeneration"
        :total="total"
        :revision="pageRevision"
        :get-row="getRow"
        :live-by-id="liveById"
        :selected="selected"
        :all-selected="allSelected"
        :now-ms="nowMs"
        :source-state="sourceState"
        :sort="sorts"
        :show-id-column="showIdColumn"
        :show-name-column="showNameColumn"
        :show-display-name-column="showDisplayNameColumn"
        :show-address-column="showAddressColumn"
        :show-comment-column="showCommentColumn"
        :show-cycle-column="showCycleColumn"
        :show-active-column="showActiveColumn"
        @update:show-id-column="onShowIdColumn"
        @update:show-name-column="onShowNameColumn"
        @update:show-display-name-column="onShowDisplayNameColumn"
        @update:show-address-column="onShowAddressColumn"
        @update:show-comment-column="onShowCommentColumn"
        @update:show-cycle-column="onShowCycleColumn"
        @update:show-active-column="onShowActiveColumn"
        @range="onRange"
        @select="onSelect"
        @select-all="onSelectAll"
        @clear-selection="onClearSelection"
        @sort="onSort"
        @chart="openChart"
        @row-context-menu="onTagRowContextMenu"
        @details="onTagDetails"
        @dismiss-context-menu="closeTagContextMenu"
      />
    </div>

    <Teleport to="body">
      <div
        v-if="tagContextMenu"
        class="tag-context-menu-backdrop"
        @click="closeTagContextMenu"
        @contextmenu.prevent="closeTagContextMenu"
      >
        <div
          class="tag-context-menu"
          role="menu"
          :style="{ left: `${tagContextMenu.x}px`, top: `${tagContextMenu.y}px` }"
          @click.stop
        >
          <button
            type="button"
            role="menuitem"
            :disabled="!selected.size || busy"
            @click="onContextDetails"
          >
            Details …
          </button>
          <button
            v-if="isAdmin"
            type="button"
            role="menuitem"
            :disabled="!tagContextMenuEnabled"
            @click="onContextSetActive(true)"
          >
            Set active
          </button>
          <button
            v-if="isAdmin"
            type="button"
            role="menuitem"
            :disabled="!tagContextMenuEnabled"
            @click="onContextSetActive(false)"
          >
            Set inactive
          </button>
          <button
            v-if="isAdmin"
            type="button"
            role="menuitem"
            :disabled="!tagContextMenuEnabled"
            @click="onContextSetCycle"
          >
            Set cycle
          </button>
          <button
            v-if="isAdmin"
            type="button"
            role="menuitem"
            :disabled="!selected.size || busy"
            @click="onContextAddToChart"
          >
            Add to chart …
          </button>
        </div>
      </div>
    </Teleport>

    <TagJobDialog
      v-if="browseOpen"
      :title="browseTitle"
      title-id="browse-title"
      :status="browseStatus"
      :lines="browseLogLines"
      :placeholder="browsePlaceholder"
      :follow="browseRunning || browsePaused"
    >
      <template #actions>
        <button class="btn secondary" type="button" :disabled="browseBusy" @click="closeBrowse">Close</button>
        <button
          class="btn secondary"
          type="button"
          :class="{ invisible: !(browseRunning || browsePaused) }"
          :disabled="browseBusy || !(browseRunning || browsePaused)"
          :tabindex="browseRunning || browsePaused ? 0 : -1"
          @click="cancelBrowse"
        >
          Cancel
        </button>
        <button
          class="btn"
          type="button"
          :disabled="browseBusy || browseRunning || browsePaused || !browseConnected"
          @click="startBrowse"
        >
          Start browse
        </button>
      </template>
    </TagJobDialog>

    <TagJobDialog
      v-if="xmlImportOpen"
      title="Import OPC-UA XML"
      title-id="xml-import-title"
      :status="xmlImportStatus"
      :lines="xmlImportLogLines"
      :placeholder="xmlImportPlaceholder"
      :follow="xmlImportActive"
    >
      <label class="tag-job-file">
        <span class="hint">XML file</span>
        <input
          ref="xmlFileEl"
          type="file"
          accept=".xml,text/xml,application/xml"
          :disabled="xmlImportBusy || xmlImportRunning || xmlImportUploading || xmlImportBlockedByBrowse"
          @change="onXmlFileSelected"
        />
      </label>
      <template #actions>
        <button class="btn secondary" type="button" :disabled="xmlImportBusy" @click="closeXmlImport">Close</button>
        <button
          class="btn secondary"
          type="button"
          :class="{ invisible: !xmlImportActive }"
          :disabled="xmlImportBusy || !xmlImportActive"
          :tabindex="xmlImportActive ? 0 : -1"
          @click="cancelXmlImport"
        >
          Cancel
        </button>
      </template>
    </TagJobDialog>

    <div v-if="cycleOpen" class="overlay">
      <div class="overlay-card">
        <strong>Set cycle in milliseconds</strong>
        <label class="field">
          <span class="hint">Cycle</span>
          <select v-model.number="cycleValue">
            <option v-for="cycle in cycles" :key="cycle" :value="cycle">{{ cycle }}</option>
          </select>
        </label>
        <div class="actions">
          <button class="btn secondary" type="button" @click="cycleOpen = false">Close</button>
          <button class="btn" type="button" :disabled="busy" @click="applyCycle">Apply</button>
        </div>
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

    <TagDetailsDialog
      v-if="detailsIds"
      :data-source-id="dataSourceId"
      :tag-ids="detailsIds"
      @updated="patchUpdated"
      @close="detailsIds = null"
    />

    <AddToChartDialog
      v-if="addToChartIds"
      :tag-ids="addToChartIds"
      @close="addToChartIds = null"
    />

    <TagChartOverlay
      v-if="chartTag"
      :key="chartTag.id"
      :data-source-id="dataSourceId"
      :tag-id="chartTag.id"
      :tag-name="chartTag.name"
      :source-name="sourceName"
      @close="chartTag = null"
    />
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from "vue";
import { useRoute } from "vue-router";
import SourceTypeIcon from "../components/SourceTypeIcon.vue";
import VirtualTagTable from "../components/VirtualTagTable.vue";
import WaitOverlay from "../components/WaitOverlay.vue";
import TagChartOverlay from "../components/TagChartOverlay.vue";
import AddToChartDialog from "../components/AddToChartDialog.vue";
import TagDetailsDialog from "../components/TagDetailsDialog.vue";
import TagJobButton from "../components/TagJobButton.vue";
import TagJobDialog from "../components/TagJobDialog.vue";
import {
  cancelOpcUaBrowse,
  cancelOpcUaXmlImport,
  createPollSession,
  getDataTagActualValues,
  getDataTags,
  getOpcUaBrowseStatus,
  getOpcUaXmlImportStatus,
  isAbortError,
  isTimeoutError,
  setDataTagsActive,
  setDataTagsCycle,
  startOpcUaBrowse,
  startOpcUaXmlImport
} from "../api";
import type {
  DataTagActualValue,
  DataTagListItem,
  OpcUaBrowseStatusLine,
  TagJobLogLine,
  TagJobStatus
} from "../types";
import {
  applyClick,
  cappedRange,
  idsInIndexRange,
  type TagSelectEvent
} from "../tagSelection";
import { formatInteger } from "../format";
import { notify } from "../notify";
import { isAdmin } from "../session";
import { cycleSort, sortQuery, type SortKey, type SortLevel } from "../tagSort";

const PAGE_SIZE = 150;
const COLUMN_PREFS_PREFIX = "tagColumns:";

type TagColumnPrefs = {
  id: boolean;
  name: boolean;
  displayName: boolean;
  address: boolean;
  comment: boolean;
  cycle: boolean;
  active: boolean;
};

function defaultColumnPrefs(): TagColumnPrefs {
  return { id: false, name: true, displayName: false, address: false, comment: false, cycle: true, active: true };
}

function readColumnPrefs(id: number): TagColumnPrefs {
  const empty = defaultColumnPrefs();
  if (!Number.isFinite(id) || id <= 0) {
    return empty;
  }
  try {
    const raw = window.localStorage.getItem(COLUMN_PREFS_PREFIX + id);
    if (!raw) {
      return empty;
    }
    const parsed = JSON.parse(raw) as Partial<TagColumnPrefs>;
    return {
      id: parsed.id === true,
      name: parsed.name !== false,
      displayName: parsed.displayName === true,
      address: parsed.address === true,
      comment: parsed.comment === true,
      cycle: parsed.cycle !== false,
      active: parsed.active !== false
    };
  } catch {
    return empty;
  }
}

function writeColumnPrefs(id: number) {
  if (!Number.isFinite(id) || id <= 0) {
    return;
  }
  try {
    const prefs: TagColumnPrefs = {
      id: showIdColumn.value,
      name: showNameColumn.value,
      displayName: showDisplayNameColumn.value,
      address: showAddressColumn.value,
      comment: showCommentColumn.value,
      cycle: showCycleColumn.value,
      active: showActiveColumn.value
    };
    window.localStorage.setItem(COLUMN_PREFS_PREFIX + id, JSON.stringify(prefs));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

const tagTableRef = ref<{
  scrollToIndex: (index: number) => void;
  scrollToTop: (index: number) => void;
  topIndex: () => number;
} | null>(null);
// The first visible row of each view of the active switch, so toggling back returns to the same place.
let restoreTopIndex: number | null = null;
const savedTopIndex = new Map<boolean, number>();
const route = useRoute();
const dataSourceId = computed(() => Number(route.params.id));

const filterText = ref("");
const activeOnly = ref(true);
const sorts = shallowRef<SortLevel[]>([]);
const showIdColumn = ref(false);
const showNameColumn = ref(true);
const showDisplayNameColumn = ref(false);
const showAddressColumn = ref(false);
const showCommentColumn = ref(false);
const showCycleColumn = ref(true);
const showActiveColumn = ref(true);
const sourceName = ref("");
const sourceState = ref("");
const sourceType = ref("");
const cycles = ref<number[]>([1000]);
const total = ref(0);
const pages = shallowRef(new Map<number, DataTagListItem[]>());
const pageRevision = ref(0);
const liveById = shallowRef(new Map<number, DataTagActualValue>());
const selected = shallowRef(new Set<number>());
const nowMs = ref(Date.now());
const error = ref("");
const busy = ref(false);
const overlayError = ref("");
const overlayTitle = ref("Loading");
const overlayMessage = ref("Loading data tags …");
const cycleOpen = ref(false);
const cycleValue = ref(1000);
const chartTag = ref<DataTagListItem | null>(null);
const addToChartIds = ref<number[] | null>(null);
const detailsIds = ref<number[] | null>(null);
const browseOpen = ref(false);
const browseBusy = ref(false);
const browseError = ref("");
const browseStatusLines = shallowRef<OpcUaBrowseStatusLine[]>([]);
const browseRunning = ref(false);
const browsePaused = ref(false);
const browseCompleted = ref(false);
const browseWaitStartedMs = ref(0);
const browseReconnectStartedMs = ref(0);
const browseLastServerLine = ref("");

const isOpcUa = computed(() => sourceType.value === "OpcUa");
const isS7 = computed(() => sourceType.value === "S7CommPlus");
// OPC UA and S7CommPlus sources import their tags with a browse job.
const canBrowse = computed(() => isOpcUa.value || isS7.value);
const browseTitle = computed(() => (isS7.value ? "Browse PLC" : "Browse OPC UA"));
const browseHeaderActive = computed(() => browseRunning.value || browsePaused.value);
const browseConnected = computed(() => sourceState.value === "Connected");
const browseDisplayLines = computed(() => {
  const lines: string[] = [];
  for (const line of browseStatusLines.value) {
    if (line.replaceLast && lines.length) {
      lines[lines.length - 1] = line.text;
    } else {
      lines.push(line.text);
    }
  }
  return lines;
});

function stripBrowseWaitSuffix(line: string) {
  return line
    .replace(/\s·\swaiting( for server)? \d+s$/, "")
    .replace(/\s·\sreconnecting \d+s$/, "");
}

const browseDisplayLinesLive = computed(() => {
  const lines = browseDisplayLines.value.map((line) => stripBrowseWaitSuffix(line));
  if (!lines.length || (!browseRunning.value && !browsePaused.value)) {
    return lines;
  }

  const last = lines[lines.length - 1];
  if (browsePaused.value) {
    const seconds = Math.max(0, Math.floor((nowMs.value - browseReconnectStartedMs.value) / 1000));
    lines[lines.length - 1] = `${last} · reconnecting ${seconds}s`;
  } else if (browseRunning.value && !browseCompleted.value) {
    const seconds = Math.max(0, Math.floor((nowMs.value - browseWaitStartedMs.value) / 1000));
    // The server reports progress every second; only a longer silence is a wait worth showing.
    if (seconds >= 5) {
      lines[lines.length - 1] = `${last} · waiting ${seconds}s`;
    }
  }

  return lines;
});

function isBrowseSuccessLine(line: string) {
  return line === "Browse completed successfully";
}

const browseLogLines = computed<TagJobLogLine[]>(() => {
  const lines = browseDisplayLinesLive.value;
  const active = browseRunning.value || browsePaused.value;
  return lines.map((text, index) => {
    const success = isBrowseSuccessLine(text);
    return { text, success, active: active && index === lines.length - 1 && !success };
  });
});

const browsePlaceholder = computed(() =>
  isS7.value
    ? [
        "Start browse to read all values of the data blocks and the PLC tags.",
        "New tags are inactive: activate the values you want to log."
      ]
    : ["Start browse to read scalar tags from the OPC UA server."]
);

const browseCancelled = computed(() => browseError.value === "The browse was cancelled.");

const browseButtonShowsError = computed(
  () => !browseRunning.value && !!browseError.value && !browseCancelled.value
);

const browseStatus = computed<TagJobStatus[]>(() => {
  if (browsePaused.value) {
    return [{ kind: "warn", text: "Connection lost" }];
  }
  if (browseRunning.value) {
    return [{ kind: "running", text: "Running …" }];
  }
  if (browseCancelled.value) {
    return [{ kind: "warn", text: "Cancelled" }];
  }
  if (browseError.value) {
    return [{ kind: "error", text: browseError.value }];
  }
  if (!browseConnected.value) {
    return [{ kind: "hint", text: "The data source is not connected." }];
  }
  return [];
});

const xmlImportOpen = ref(false);
const xmlImportBusy = ref(false);
const xmlImportError = ref("");
const xmlImportStatusLines = shallowRef<OpcUaBrowseStatusLine[]>([]);
const xmlImportRunning = ref(false);
const xmlImportCompleted = ref(false);
const xmlImportUploading = ref(false);
// The file being uploaded; the server knows nothing of the import until the upload is complete.
const xmlUpload = shallowRef<{ name: string; loaded: number; total: number; startedMs: number } | null>(null);
const xmlFileEl = ref<HTMLInputElement | null>(null);

function formatMegabytes(bytes: number) {
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatRemaining(seconds: number) {
  if (seconds < 60) {
    return `${Math.max(1, Math.ceil(seconds))} s`;
  }
  if (seconds < 3600) {
    return `${Math.max(1, Math.ceil(seconds / 60))} min`;
  }
  return `${(seconds / 3600).toFixed(1)} h`;
}

// Like ShortFileName of the server: the middle of a long name goes, so the sizes and times stay visible.
function shortFileName(name: string) {
  const max = 28;
  return name.length <= max ? name : `${name.slice(0, 12)}…${name.slice(name.length - (max - 13))}`;
}

// Written like the lines of the server, which take over when the upload is complete.
const xmlUploadLine = computed<OpcUaBrowseStatusLine | null>(() => {
  const upload = xmlUpload.value;
  if (!upload) {
    return null;
  }
  const head = `Upload ${shortFileName(upload.name)} …`;
  if (upload.total > 0 && upload.loaded >= upload.total) {
    return { text: `${head} ${formatMegabytes(upload.total)} · starting the import`, replaceLast: true, percent: null };
  }
  const percent = upload.total > 0 ? (100 * upload.loaded) / upload.total : 0;
  const parts = [`${Math.floor(percent)} %`, `${formatMegabytes(upload.loaded)} / ${formatMegabytes(upload.total)}`];
  const seconds = (Date.now() - upload.startedMs) / 1000;
  if (seconds >= 2 && upload.loaded > 0) {
    const rate = upload.loaded / seconds;
    // Only the time left, like the server lines: the line has to fit the dialog.
    parts.push(`~${formatRemaining((upload.total - upload.loaded) / rate)} left`);
  }
  return { text: `${head} ${parts.join(" · ")}`, replaceLast: true, percent };
});

// The server already updates the running line in place, so the list is shown as-is.
const xmlImportDisplayLines = computed<OpcUaBrowseStatusLine[]>(() => {
  const upload = xmlUploadLine.value;
  if (xmlImportUploading.value && upload) {
    return [upload];
  }
  return xmlImportStatusLines.value;
});

function isXmlImportSuccessLine(line: string) {
  return line.startsWith("Import completed successfully") || line.endsWith(" · done");
}

const xmlImportLogLines = computed<TagJobLogLine[]>(() => {
  const lines = xmlImportDisplayLines.value;
  return lines.map((line, index) => {
    const success = isXmlImportSuccessLine(line.text);
    const active = xmlImportActive.value && index === lines.length - 1 && !success;
    return { text: line.text, success, active, percent: line.percent };
  });
});

const xmlImportPlaceholder = [
  "Choose an OPC UA nodeset XML file to import scalar tags.",
  "The import starts at once.",
  "Tags that are not in the file are marked deleted."
];

const xmlImportCancelled = computed(() => xmlImportError.value === "The XML import was cancelled.");

const xmlImportActive = computed(() => xmlImportRunning.value || xmlImportUploading.value);

// The server rejects an import while a browse of the source runs.
const xmlImportBlockedByBrowse = computed(() => browseHeaderActive.value && !xmlImportActive.value);

const xmlImportButtonShowsError = computed(
  () => !xmlImportActive.value && !!xmlImportError.value && !xmlImportCancelled.value
);

const xmlImportStatus = computed<TagJobStatus[]>(() => {
  const status: TagJobStatus[] = [];
  if (xmlImportUploading.value) {
    status.push({ kind: "running", text: "Uploading …" });
  } else if (xmlImportRunning.value) {
    status.push({ kind: "running", text: "Running …" });
  }
  if (xmlImportRunning.value && xmlImportError.value) {
    status.push({ kind: "error", text: xmlImportError.value });
  } else if (xmlImportCancelled.value) {
    status.push({ kind: "warn", text: "Cancelled" });
  } else if (xmlImportError.value) {
    status.push({ kind: "error", text: xmlImportError.value });
  } else if (xmlImportBlockedByBrowse.value) {
    status.push({ kind: "hint", text: "A browse is running. Wait for it to finish or cancel it before importing XML." });
  }
  return status;
});

const valueSession = createPollSession();
let actionController: AbortController | null = null;
let browseController: AbortController | null = null;
let browsePollTimer: number | undefined;
let browseUiTickTimer: number | undefined;
// Status requests; the upload has a controller of its own, so a poll never aborts it.
let xmlImportController: AbortController | null = null;
let xmlUploadController: AbortController | null = null;
let xmlImportPollTimer: number | undefined;
let debounceTimer: number | undefined;
let pollTimer: number | undefined;
let visibleStart = 0;
let visibleEnd = -1;
let visibleIds: number[] = [];
let anchorIndex = -1;
let focusIndex = -1;
let pendingNudgeIndex: number | null = null;
let pendingNudgeDelta = 1;
let rangeEpoch = 0;
let fillEpoch = -1;
let rangeFillActive = false;
let dragRaf = 0;
let pendingSelect: TagSelectEvent | null = null;
let stopped = false;
let shownPreparePercent = -1;
const filterGeneration = ref(0);
const pagePromises = new Map<number, Promise<void>>();
const pageControllers = new Map<number, AbortController>();
const pageRetryTimers = new Map<number, number>();

const writableSelected = computed(() => {
  const ids: number[] = [];
  for (const id of selected.value) {
    const row = findCached(id);
    if (row && !row.isSys) {
      ids.push(id);
    }
  }
  return ids;
});

const tagContextMenu = ref<{ x: number; y: number } | null>(null);
const tagContextMenuEnabled = computed(() => writableSelected.value.length > 0 && !busy.value);

const countLabel = computed(() => {
  const count = formatInteger(total.value);
  if (activeOnly.value) {
    return total.value === 1 ? `${count} active tag` : `${count} active tags`;
  }
  return total.value === 1 ? `${count} tag total` : `${count} tags total`;
});

const countTooltip = computed(() => {
  if (activeOnly.value) {
    return "Only active data tags are listed. Turn the switch off to include inactive tags.";
  }
  return "All data tags are listed. Turn the switch on to show only active tags.";
});

const allSelected = computed(() => {
  if (total.value <= 0 || selected.value.size === 0) {
    return false;
  }
  if (isRangeFillCurrent() && anchorIndex === 0 && focusIndex === total.value - 1) {
    return true;
  }
  const { from, to } = cappedRange(0, total.value - 1, total.value);
  let writable = 0;
  for (let i = from; i <= to; i++) {
    const row = getRow(i);
    if (!row) {
      return false;
    }
    if (row.isSys) {
      continue;
    }
    writable++;
    if (!selected.value.has(row.id)) {
      return false;
    }
  }
  return writable > 0;
});

function getRow(index: number): DataTagListItem | undefined {
  const pageIndex = Math.floor(index / PAGE_SIZE);
  const rows = pages.value.get(pageIndex);
  if (!rows) {
    return undefined;
  }
  return rows[index % PAGE_SIZE];
}

function findCached(id: number): DataTagListItem | undefined {
  for (const rows of pages.value.values()) {
    const found = rows.find((row) => row.id === id);
    if (found) {
      return found;
    }
  }
  return undefined;
}

function abortPageLoads() {
  clearPageRetryTimers();
  for (const controller of pageControllers.values()) {
    controller.abort();
  }
  pageControllers.clear();
  pagePromises.clear();
}

function clearPageRetryTimers() {
  for (const timer of pageRetryTimers.values()) {
    window.clearTimeout(timer);
  }
  pageRetryTimers.clear();
}

function clearPageRetryTimer(pageIndex: number) {
  const timer = pageRetryTimers.get(pageIndex);
  if (timer !== undefined) {
    window.clearTimeout(timer);
    pageRetryTimers.delete(pageIndex);
  }
}

function schedulePageRetry(pageIndex: number, generation: number, delayMs: number) {
  clearPageRetryTimer(pageIndex);
  const timer = window.setTimeout(() => {
    pageRetryTimers.delete(pageIndex);
    if (generation !== filterGeneration.value) {
      return;
    }
    void loadPage(pageIndex, generation);
  }, delayMs);
  pageRetryTimers.set(pageIndex, timer);
}

function clearDragSelect() {
  if (dragRaf) {
    window.cancelAnimationFrame(dragRaf);
    dragRaf = 0;
  }
  pendingSelect = null;
}

function resetList() {
  abortPageLoads();
  clearDragSelect();
  pages.value = new Map();
  pageRevision.value += 1;
  liveById.value = new Map();
  selected.value = new Set();
  anchorIndex = -1;
  focusIndex = -1;
  pendingNudgeIndex = null;
  rangeFillActive = false;
  rangeEpoch += 1;
  fillEpoch = -1;
  visibleIds = [];
  shownPreparePercent = -1;
}

function loadPage(pageIndex: number, generation: number): Promise<void> {
  if (generation !== filterGeneration.value || pages.value.has(pageIndex)) {
    return Promise.resolve();
  }
  const pending = pagePromises.get(pageIndex);
  if (pending) {
    return pending;
  }
  const promise = fetchPage(pageIndex, generation).finally(() => {
    if (pagePromises.get(pageIndex) === promise) {
      pagePromises.delete(pageIndex);
    }
  });
  pagePromises.set(pageIndex, promise);
  return promise;
}

async function fetchPage(pageIndex: number, generation: number) {
  const controller = new AbortController();
  pageControllers.set(pageIndex, controller);
  if (pageIndex === 0 && pages.value.size === 0 && shownPreparePercent < 0) {
    busy.value = true;
    overlayError.value = "";
    overlayTitle.value = "Loading";
    overlayMessage.value = "Loading data tags …";
  }

  try {
    const data = await getDataTags(
      dataSourceId.value,
      {
        q: filterText.value,
        activeOnly: activeOnly.value,
        offset: pageIndex * PAGE_SIZE,
        limit: PAGE_SIZE,
        ...sortQuery(sorts.value)
      },
      controller.signal
    );
    if (generation !== filterGeneration.value) {
      return;
    }

    sourceName.value = data.dataSourceName;
    sourceState.value = data.dataSourceState ?? "";
    sourceType.value = data.dataSourceType ?? sourceType.value;
    cycles.value = data.cycles.length ? data.cycles : [1000];

    if (data.preparing) {
      const percent = Math.max(0, Math.min(100, data.preparePercent ?? 0));
      busy.value = true;
      overlayError.value = "";
      overlayTitle.value = "Loading";
      if (percent > shownPreparePercent) {
        shownPreparePercent = percent;
        overlayMessage.value = `Preparing tag list … ${percent} %`;
      }
      schedulePageRetry(pageIndex, generation, 500);
      return;
    }

    shownPreparePercent = -1;
    total.value = data.total;
    if (pageIndex === 0 && restoreTopIndex !== null) {
      const top = Math.min(restoreTopIndex, Math.max(0, data.total - 1));
      restoreTopIndex = null;
      if (top > 0) {
        void nextTick(() => tagTableRef.value?.scrollToTop(top));
      }
    }
    const next = new Map(pages.value);
    next.set(pageIndex, data.items);
    pages.value = next;
    pageRevision.value += 1;
    error.value = "";
    overlayError.value = "";
    busy.value = false;
    if (pageOverlapsSelectionWindow(pageIndex)) {
      syncActiveRangeSelection();
    }
    applyPendingNudge();
    ensurePages(visibleStart, visibleEnd, generation);
    if (pageIndex === 0 && canBrowse.value) {
      void syncBrowseHeaderStatus();
    }
    if (pageIndex === 0 && sourceType.value === "OpcUa") {
      void syncXmlImportHeaderStatus();
    }
  } catch (err) {
    if (generation !== filterGeneration.value) {
      return;
    }
    if (isAbortError(err) && !isTimeoutError(err)) {
      return;
    }
    if (isTimeoutError(err) && pageIndex === 0 && pages.value.size === 0) {
      busy.value = true;
      overlayError.value = "";
      overlayTitle.value = "Loading";
      if (shownPreparePercent < 0) {
        overlayMessage.value = "The device is still busy, retrying …";
      }
      schedulePageRetry(pageIndex, generation, 1000);
      return;
    }
    const message = err instanceof Error ? err.message : "Unknown error";
    error.value = message;
    if (pages.value.size === 0) {
      overlayError.value = message;
      overlayTitle.value = "Error";
      busy.value = true;
    } else {
      busy.value = false;
    }
  } finally {
    if (pageControllers.get(pageIndex) === controller) {
      pageControllers.delete(pageIndex);
    }
  }
}

function ensurePages(start: number, end: number, generation: number) {
  if (generation !== filterGeneration.value) {
    return;
  }
  if (!pages.value.has(0) && !pagePromises.has(0)) {
    void loadPage(0, generation);
    if (end < 0) {
      return;
    }
  }
  if (end < start) {
    return;
  }
  const from = Math.floor(Math.max(start, 0) / PAGE_SIZE);
  const to = Math.floor(Math.max(end, 0) / PAGE_SIZE);
  for (let page = from; page <= to; page++) {
    if (!pages.value.has(page)) {
      void loadPage(page, generation);
    }
  }
}

function requestRangePages(from: number, to: number) {
  if (to < from) {
    return;
  }
  const generation = filterGeneration.value;
  const fromPage = Math.floor(from / PAGE_SIZE);
  const toPage = Math.floor(to / PAGE_SIZE);
  for (let page = fromPage; page <= toPage; page++) {
    void loadPage(page, generation);
  }
}

function pageOverlapsSelectionWindow(pageIndex: number): boolean {
  if (!isRangeFillCurrent()) {
    return false;
  }
  const { from, to } = cappedRange(anchorIndex, focusIndex, total.value);
  if (to < from) {
    return false;
  }
  const start = pageIndex * PAGE_SIZE;
  const end = start + PAGE_SIZE - 1;
  return start <= to && end >= from;
}

function isRangeFillCurrent(): boolean {
  return rangeFillActive && fillEpoch === rangeEpoch && anchorIndex >= 0;
}

function syncActiveRangeSelection() {
  if (!isRangeFillCurrent()) {
    return;
  }
  const { from, to } = cappedRange(anchorIndex, focusIndex, total.value);
  selected.value = new Set(idsInIndexRange(getRow, from, to));
}

function onRange(start: number, end: number, ids: number[]) {
  visibleStart = start;
  visibleEnd = end;
  visibleIds = ids;
  ensurePages(start, end, filterGeneration.value);
}

function onSelect(event: TagSelectEvent) {
  if (event.dragging) {
    pendingSelect = event;
    if (!dragRaf) {
      dragRaf = window.requestAnimationFrame(flushPendingSelect);
    }
    return;
  }
  clearDragSelect();
  commitSelect(event);
}

function flushPendingSelect() {
  dragRaf = 0;
  if (!pendingSelect) {
    return;
  }
  const event = pendingSelect;
  pendingSelect = null;
  commitSelect(event);
}

function commitSelect(event: TagSelectEvent) {
  pendingNudgeIndex = null;
  if (event.mode === "range") {
    if (anchorIndex < 0) {
      const fallback = applyClick({
        mode: "replace",
        index: event.index,
        anchor: -1,
        total: total.value,
        selected: selected.value,
        getRow
      });
      selected.value = fallback.selected;
      anchorIndex = fallback.anchor;
      focusIndex = event.index;
      if (!event.dragging) {
        return;
      }
    }
    if (!rangeFillActive) {
      rangeEpoch += 1;
      fillEpoch = rangeEpoch;
      rangeFillActive = true;
    }
    focusIndex = event.index;
    const { from, to } = cappedRange(anchorIndex, focusIndex, total.value);
    selected.value = new Set(idsInIndexRange(getRow, from, to));
    requestRangePages(from, to);
    return;
  }

  rangeFillActive = false;
  rangeEpoch += 1;
  const result = applyClick({
    mode: event.mode,
    index: event.index,
    anchor: anchorIndex,
    total: total.value,
    selected: selected.value,
    getRow
  });
  selected.value = result.selected;
  anchorIndex = result.anchor;
  focusIndex = event.index;
}

function stepSelectable(origin: number, delta: number): { index: number; pending: boolean } | null {
  let index = origin;
  while (true) {
    index += delta;
    if (index < 0 || index >= total.value) {
      return null;
    }
    const row = getRow(index);
    if (!row) {
      return { index, pending: true };
    }
    if (!row.isSys) {
      return { index, pending: false };
    }
  }
}

function onNudge(delta: number) {
  if (selected.value.size === 0 || total.value <= 0) {
    return;
  }
  closeTagContextMenu();
  const origin = pendingNudgeIndex ?? focusIndex;
  if (origin < 0) {
    return;
  }
  const found = stepSelectable(origin, delta);
  if (!found) {
    return;
  }
  tagTableRef.value?.scrollToIndex(found.index);
  if (found.pending) {
    pendingNudgeIndex = found.index;
    pendingNudgeDelta = delta;
    void loadPage(Math.floor(found.index / PAGE_SIZE), filterGeneration.value).then(() => applyPendingNudge());
    return;
  }
  pendingNudgeIndex = null;
  commitSelect({ index: found.index, mode: "replace", dragging: false });
}

function applyPendingNudge() {
  if (pendingNudgeIndex == null) {
    return;
  }
  const index = pendingNudgeIndex;
  const delta = pendingNudgeDelta;
  const row = getRow(index);
  if (!row) {
    return;
  }
  pendingNudgeIndex = null;
  if (row.isSys) {
    const found = stepSelectable(index, delta);
    if (!found) {
      return;
    }
    tagTableRef.value?.scrollToIndex(found.index);
    if (found.pending) {
      pendingNudgeIndex = found.index;
      pendingNudgeDelta = delta;
      void loadPage(Math.floor(found.index / PAGE_SIZE), filterGeneration.value).then(() => applyPendingNudge());
      return;
    }
    commitSelect({ index: found.index, mode: "replace", dragging: false });
    return;
  }
  commitSelect({ index, mode: "replace", dragging: false });
}

function reloadList() {
  filterGeneration.value += 1;
  resetList();
  total.value = 0;
  void loadPage(0, filterGeneration.value);
}

function toggleActiveOnly() {
  // The filter stays and the list keeps its position.
  const top = tagTableRef.value?.topIndex() ?? Math.max(0, visibleStart);
  savedTopIndex.set(activeOnly.value, top);
  activeOnly.value = !activeOnly.value;
  restoreTopIndex = savedTopIndex.get(activeOnly.value) ?? top;
}

function onSort(key: SortKey) {
  sorts.value = cycleSort(sorts.value, key);
  reloadList();
}

function onSelectAll() {
  if (total.value <= 0) {
    return;
  }
  clearDragSelect();
  rangeEpoch += 1;
  fillEpoch = rangeEpoch;
  rangeFillActive = true;
  anchorIndex = 0;
  focusIndex = total.value - 1;
  const { from, to } = cappedRange(anchorIndex, focusIndex, total.value);
  selected.value = new Set(idsInIndexRange(getRow, from, to));
  requestRangePages(from, to);
}

function onClearSelection() {
  clearDragSelect();
  rangeFillActive = false;
  rangeEpoch += 1;
  selected.value = new Set();
  anchorIndex = -1;
  focusIndex = -1;
  pendingNudgeIndex = null;
}

function patchUpdated(updated: DataTagListItem[]) {
  if (updated.length === 0) {
    return;
  }
  const byId = new Map(updated.map((row) => [row.id, row]));
  const next = new Map<number, DataTagListItem[]>();
  for (const [page, rows] of pages.value) {
    next.set(page, rows.map((row) => byId.get(row.id) ?? row));
  }
  pages.value = next;
  pageRevision.value += 1;
}

function cancelCurrent() {
  abortPageLoads();
  valueSession.abort();
  actionController?.abort();
  busy.value = false;
  overlayError.value = "The request was cancelled.";
}

async function setActive(isActive: boolean) {
  const ids = writableSelected.value;
  if (ids.length === 0) {
    return;
  }
  actionController?.abort();
  actionController = new AbortController();
  busy.value = true;
  overlayError.value = "";
  overlayTitle.value = isActive ? "Set active" : "Set inactive";
  overlayMessage.value = `Updating ${ids.length} data tags …`;
  error.value = "";
  try {
    const result = await setDataTagsActive(dataSourceId.value, ids, isActive, actionController.signal);
    patchUpdated(result.updated);
    busy.value = false;
    if (result.notActivated) {
      notify("warning", "Not all activated",
        `${formatInteger(result.notActivated)} of ${formatInteger(ids.length)} data tags stay inactive: strings and system types cannot be logged.`);
    }
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    const message = err instanceof Error ? err.message : "Update failed.";
    error.value = message;
    overlayError.value = message;
    overlayTitle.value = "Error";
    busy.value = true;
  }
}

function openCycle() {
  const first = findCached(writableSelected.value[0]);
  cycleValue.value = first?.cycle ?? cycles.value[0] ?? 1000;
  cycleOpen.value = true;
}

function onTagRowContextMenu(payload: { index: number; clientX: number; clientY: number }) {
  const row = getRow(payload.index);
  if (!row || row.isSys) {
    return;
  }
  if (!selected.value.has(row.id)) {
    selected.value = new Set([row.id]);
    anchorIndex = payload.index;
    focusIndex = payload.index;
  }
  // A viewer sees only Details in the menu
  tagContextMenu.value = { x: payload.clientX, y: payload.clientY };
}

function closeTagContextMenu() {
  tagContextMenu.value = null;
}

function onContextSetActive(isActive: boolean) {
  if (!tagContextMenuEnabled.value) {
    return;
  }
  closeTagContextMenu();
  void setActive(isActive);
}

function onContextSetCycle() {
  if (!tagContextMenuEnabled.value) {
    return;
  }
  closeTagContextMenu();
  openCycle();
}

// A double click shows the details of its row only; the details of several tags open from the context menu.
// A system tag cannot be selected; its details open read-only.
function onTagDetails(index: number) {
  const row = getRow(index);
  if (row && !busy.value) {
    detailsIds.value = [row.id];
  }
}

function onContextDetails() {
  closeTagContextMenu();
  if (selected.value.size > 0 && !busy.value) {
    detailsIds.value = [...selected.value];
  }
}

function openChart(row: DataTagListItem) {
  chartTag.value = row;
}

function openAddToChart() {
  if (selected.value.size > 0 && !busy.value) {
    addToChartIds.value = [...selected.value];
  }
}

function onContextAddToChart() {
  closeTagContextMenu();
  openAddToChart();
}

function applyBrowseStatus(status: {
  running?: boolean;
  paused?: boolean;
  completed?: boolean;
  error?: string | null;
  lines?: OpcUaBrowseStatusLine[];
}) {
  const wasActive = browseRunning.value || browsePaused.value;
  const wasPaused = browsePaused.value;
  const serverLines = status.lines ?? [];
  const newLastLine = stripBrowseWaitSuffix(
    serverLines.length > 0 ? (serverLines[serverLines.length - 1].text ?? "") : ""
  );

  browseRunning.value = !!status.running;
  browsePaused.value = !!status.paused;
  browseCompleted.value = !!status.completed;
  browseStatusLines.value = serverLines;
  browseError.value = status.error ?? "";

  const active = browseRunning.value || browsePaused.value;
  if (active) {
    if (!wasActive || newLastLine !== browseLastServerLine.value) {
      browseWaitStartedMs.value = Date.now();
    }
    if (browsePaused.value && !wasPaused) {
      browseReconnectStartedMs.value = Date.now();
    }
    browseLastServerLine.value = newLastLine;
    startBrowseUiTick();
  } else {
    browseLastServerLine.value = "";
    stopBrowseUiTick();
  }
}

function startBrowseUiTick() {
  if (browseUiTickTimer !== undefined) {
    return;
  }

  browseUiTickTimer = window.setInterval(() => {
    nowMs.value = Date.now();
  }, 500);
}

function stopBrowseUiTick() {
  if (browseUiTickTimer !== undefined) {
    window.clearInterval(browseUiTickTimer);
    browseUiTickTimer = undefined;
  }
}

function resetBrowseHeaderState() {
  stopBrowsePoll();
  browseController?.abort();
  browseRunning.value = false;
  browsePaused.value = false;
  browseCompleted.value = false;
  browseError.value = "";
  browseStatusLines.value = [];
  stopBrowseUiTick();
}

async function syncBrowseHeaderStatus() {
  if (!canBrowse.value || stopped) {
    return;
  }
  const controller = new AbortController();
  try {
    const status = await getOpcUaBrowseStatus(dataSourceId.value, controller.signal);
    if (stopped || !canBrowse.value) {
      return;
    }
    applyBrowseStatus(status);
    if (status.running || status.paused) {
      scheduleBrowsePoll();
    }
  } catch (err) {
    if (stopped) {
      return;
    }
    if (isTimeoutError(err) && (browseRunning.value || browsePaused.value)) {
      scheduleBrowsePoll();
      return;
    }
    if (!isAbortError(err)) {
      // Ignore transient header sync failures; the browse dialog loads full status on open.
    }
  }
}

function stopBrowsePoll() {
  if (browsePollTimer !== undefined) {
    window.clearTimeout(browsePollTimer);
    browsePollTimer = undefined;
  }
}

function scheduleBrowsePoll() {
  stopBrowsePoll();
  if (stopped || (!browseOpen.value && !browseRunning.value && !browsePaused.value)) {
    return;
  }
  browsePollTimer = window.setTimeout(() => {
    void pollBrowseStatus();
  }, 500);
}

async function pollBrowseStatus() {
  if (stopped || (!browseOpen.value && !browseRunning.value && !browsePaused.value)) {
    return;
  }
  browseController?.abort();
  browseController = new AbortController();
  try {
    const status = await getOpcUaBrowseStatus(dataSourceId.value, browseController.signal);
    if (stopped) {
      return;
    }
    const wasRunning = browseRunning.value;
    applyBrowseStatus(status);
    if (status.completed && wasRunning) {
      reloadList();
    }
    if (status.running || status.paused) {
      scheduleBrowsePoll();
    }
  } catch (err) {
    if (stopped) {
      return;
    }
    if (isAbortError(err) && !isTimeoutError(err)) {
      return;
    }
    if (isTimeoutError(err) && (browseRunning.value || browsePaused.value)) {
      scheduleBrowsePoll();
      return;
    }
    if (!browseRunning.value && !browsePaused.value) {
      browseError.value = err instanceof Error ? err.message : "Browse status failed.";
    }
    if (browseRunning.value || browsePaused.value) {
      scheduleBrowsePoll();
    }
  }
}

async function openBrowse() {
  closeXmlImport();
  browseOpen.value = true;
  browseController?.abort();
  browseController = new AbortController();
  try {
    const status = await getOpcUaBrowseStatus(dataSourceId.value, browseController.signal);
    applyBrowseStatus(status);
    if (status.running || status.paused) {
      scheduleBrowsePoll();
    }
  } catch (err) {
    if (isAbortError(err) && !isTimeoutError(err)) {
      return;
    }
    if (isTimeoutError(err) && (browseRunning.value || browsePaused.value)) {
      scheduleBrowsePoll();
      return;
    }
    if (!isAbortError(err)) {
      browseError.value = err instanceof Error ? err.message : "Unable to load browse status.";
    }
  }
}

function closeBrowse() {
  browseOpen.value = false;
  stopBrowsePoll();
  browseController?.abort();
  browseBusy.value = false;
  if (browseRunning.value || browsePaused.value) {
    scheduleBrowsePoll();
  }
}

async function startBrowse() {
  if (browseRunning.value || browsePaused.value || !browseConnected.value) {
    return;
  }
  browseBusy.value = true;
  browseError.value = "";
  browseCompleted.value = false;
  browseController?.abort();
  browseController = new AbortController();
  try {
    const status = await startOpcUaBrowse(dataSourceId.value, browseController.signal);
    applyBrowseStatus(status);
    if (status.completed) {
      reloadList();
    } else if (status.running || status.paused) {
      scheduleBrowsePoll();
    }
  } catch (err) {
    if (isAbortError(err) && !isTimeoutError(err)) {
      return;
    }
    if (!isAbortError(err)) {
      browseError.value = err instanceof Error ? err.message : "Browse could not be started.";
    }
  } finally {
    browseBusy.value = false;
  }
}

async function cancelBrowse() {
  browseBusy.value = true;
  browseController?.abort();
  browseController = new AbortController();
  try {
    await cancelOpcUaBrowse(dataSourceId.value, browseController.signal);
    scheduleBrowsePoll();
  } catch (err) {
    if (isAbortError(err) && !isTimeoutError(err)) {
      return;
    }
    if (!isAbortError(err)) {
      browseError.value = err instanceof Error ? err.message : "Browse could not be cancelled.";
    }
  } finally {
    browseBusy.value = false;
  }
}

function applyXmlImportStatus(status: {
  running?: boolean;
  completed?: boolean;
  error?: string | null;
  lines?: OpcUaBrowseStatusLine[];
}) {
  if (xmlImportUploading.value) {
    // The status of the server still describes the previous import.
    return;
  }
  xmlImportRunning.value = !!status.running;
  xmlImportCompleted.value = !!status.completed;
  xmlImportStatusLines.value = status.lines ?? [];
  xmlImportError.value = status.error ?? "";
}

function stopXmlImportPoll() {
  if (xmlImportPollTimer !== undefined) {
    window.clearTimeout(xmlImportPollTimer);
    xmlImportPollTimer = undefined;
  }
}

function resetXmlImportHeaderState() {
  stopXmlImportPoll();
  xmlImportController?.abort();
  xmlUploadController?.abort();
  xmlImportRunning.value = false;
  xmlImportCompleted.value = false;
  xmlImportUploading.value = false;
  xmlUpload.value = null;
  xmlImportError.value = "";
  xmlImportStatusLines.value = [];
  xmlImportBusy.value = false;
}

async function syncXmlImportHeaderStatus() {
  if (!isOpcUa.value || stopped) {
    return;
  }
  const controller = new AbortController();
  try {
    const status = await getOpcUaXmlImportStatus(dataSourceId.value, controller.signal);
    if (stopped || !isOpcUa.value) {
      return;
    }
    applyXmlImportStatus(status);
    if (status.running) {
      scheduleXmlImportPoll();
    }
  } catch (err) {
    if (stopped) {
      return;
    }
    if (isTimeoutError(err) && (xmlImportRunning.value || xmlImportUploading.value)) {
      scheduleXmlImportPoll();
      return;
    }
    if (!isAbortError(err)) {
      // Ignore transient header sync failures; the import dialog loads full status on open.
    }
  }
}

function scheduleXmlImportPoll() {
  stopXmlImportPoll();
  if (stopped || xmlImportUploading.value || (!xmlImportOpen.value && !xmlImportRunning.value)) {
    return;
  }
  xmlImportPollTimer = window.setTimeout(() => {
    void pollXmlImportStatus();
  }, 1000);
}

async function pollXmlImportStatus() {
  if (stopped || xmlImportUploading.value || (!xmlImportOpen.value && !xmlImportRunning.value)) {
    return;
  }
  xmlImportController?.abort();
  xmlImportController = new AbortController();
  try {
    const status = await getOpcUaXmlImportStatus(dataSourceId.value, xmlImportController.signal);
    if (stopped) {
      return;
    }
    const wasRunning = xmlImportRunning.value;
    applyXmlImportStatus(status);
    if (status.completed && wasRunning) {
      reloadList();
    }
    if (status.running) {
      scheduleXmlImportPoll();
    }
  } catch (err) {
    if (stopped || xmlImportUploading.value) {
      return;
    }
    if (isAbortError(err) && !isTimeoutError(err)) {
      return;
    }
    if (isTimeoutError(err) && xmlImportRunning.value) {
      scheduleXmlImportPoll();
      return;
    }
    if (!xmlImportRunning.value) {
      xmlImportError.value = "No status from the device.";
    }
    if (xmlImportRunning.value) {
      scheduleXmlImportPoll();
    }
  }
}

async function openXmlImport() {
  closeBrowse();
  xmlImportOpen.value = true;
  if (xmlImportUploading.value) {
    // The upload goes on; its line is shown until the server takes over.
    return;
  }
  if (!xmlImportRunning.value) {
    xmlImportError.value = "";
  }
  xmlImportController?.abort();
  xmlImportController = new AbortController();
  try {
    const status = await getOpcUaXmlImportStatus(dataSourceId.value, xmlImportController.signal);
    applyXmlImportStatus(status);
    if (status.running) {
      scheduleXmlImportPoll();
    }
  } catch (err) {
    if (!isAbortError(err)) {
      xmlImportError.value = err instanceof Error ? err.message : "Unable to load XML import status.";
    }
  }
}

function closeXmlImport() {
  xmlImportOpen.value = false;
  stopXmlImportPoll();
  xmlImportBusy.value = false;
  if (xmlImportRunning.value) {
    scheduleXmlImportPoll();
  } else {
    xmlImportController?.abort();
  }
  if (xmlFileEl.value) {
    xmlFileEl.value.value = "";
  }
}

async function onXmlFileSelected(event: Event) {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file || xmlImportRunning.value || xmlImportUploading.value) {
    return;
  }
  if (!file.name.toLowerCase().endsWith(".xml")) {
    xmlImportError.value = "Only XML files can be imported.";
    input.value = "";
    return;
  }

  // Not busy: Close and Cancel stay available while the file is sent.
  stopXmlImportPoll();
  xmlImportController?.abort();
  xmlImportRunning.value = false;
  xmlImportError.value = "";
  xmlImportCompleted.value = false;
  xmlImportStatusLines.value = [];
  xmlUpload.value = { name: file.name, loaded: 0, total: file.size, startedMs: Date.now() };
  xmlImportUploading.value = true;
  xmlUploadController?.abort();
  const controller = new AbortController();
  xmlUploadController = controller;
  try {
    const status = await startOpcUaXmlImport(
      dataSourceId.value,
      file,
      (loaded, total) => {
        const upload = xmlUpload.value;
        if (upload && xmlUploadController === controller) {
          xmlUpload.value = { ...upload, loaded, total };
        }
      },
      controller.signal
    );
    xmlImportUploading.value = false;
    applyXmlImportStatus(status);
    if (status.completed) {
      reloadList();
    } else {
      scheduleXmlImportPoll();
    }
  } catch (err) {
    xmlImportUploading.value = false;
    if (isAbortError(err)) {
      if (xmlUploadController === controller && !stopped) {
        xmlImportError.value = "The XML import was cancelled.";
      }
    } else {
      xmlImportError.value = err instanceof Error ? err.message : "XML import could not be started.";
    }
  } finally {
    if (xmlUploadController === controller) {
      xmlUploadController = null;
      xmlUpload.value = null;
    }
    input.value = "";
  }
}

async function cancelXmlImport() {
  if (xmlImportUploading.value) {
    // The upload settles with an abort error and shows the cancelled state.
    xmlUploadController?.abort();
    return;
  }
  xmlImportBusy.value = true;
  xmlImportController?.abort();
  xmlImportController = new AbortController();
  try {
    await cancelOpcUaXmlImport(dataSourceId.value, xmlImportController.signal);
    scheduleXmlImportPoll();
  } catch (err) {
    if (!isAbortError(err)) {
      xmlImportError.value = err instanceof Error ? err.message : "XML import could not be cancelled.";
    }
  } finally {
    xmlImportBusy.value = false;
  }
}

async function applyCycle() {
  const ids = writableSelected.value;
  if (ids.length === 0) {
    cycleOpen.value = false;
    return;
  }
  actionController?.abort();
  actionController = new AbortController();
  busy.value = true;
  overlayError.value = "";
  overlayTitle.value = "Set cycle";
  overlayMessage.value = `Updating ${ids.length} data tags …`;
  error.value = "";
  try {
    const result = await setDataTagsCycle(dataSourceId.value, ids, cycleValue.value, actionController.signal);
    patchUpdated(result.updated);
    cycleOpen.value = false;
    busy.value = false;
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    const message = err instanceof Error ? err.message : "Update failed.";
    error.value = message;
    overlayError.value = message;
    overlayTitle.value = "Error";
    busy.value = true;
  }
}

async function pollValues() {
  if (stopped) {
    return;
  }
  nowMs.value = Date.now();
  if (document.hidden || visibleIds.length === 0) {
    schedulePoll();
    return;
  }

  const session = valueSession.begin();
  try {
    const data = await getDataTagActualValues(dataSourceId.value, visibleIds, session.signal);
    if (stopped) {
      return;
    }
    if (session.isCurrent()) {
      const live = new Map((data.values ?? []).map((value) => [value.id, value]));
      for (const issue of data.issues ?? []) {
        const value = live.get(issue.id);
        live.set(issue.id, value ? { ...value, issue: issue.issue } : { id: issue.id, value: 0, time: "", state: "Invalid", issue: issue.issue });
      }
      liveById.value = live;
      if (data.dataSourceState) {
        sourceState.value = data.dataSourceState;
      }
    }
  } catch (err) {
    if (stopped) {
      return;
    }
    if (!isAbortError(err)) {
      // Keep the last known values; the next tick retries.
    }
  }
  schedulePoll();
}

function schedulePoll() {
  if (stopped) {
    return;
  }
  pollTimer = window.setTimeout(() => {
    void pollValues();
  }, 1000);
}

// Another filter is another list: the remembered positions no longer fit.
watch(filterText, () => {
  savedTopIndex.clear();
  restoreTopIndex = null;
});

watch([filterText, activeOnly], () => {
  if (debounceTimer !== undefined) {
    window.clearTimeout(debounceTimer);
  }
  debounceTimer = window.setTimeout(() => {
    reloadList();
  }, 300);
});

function onShowIdColumn(value: boolean) {
  showIdColumn.value = value;
  writeColumnPrefs(dataSourceId.value);
}

function onShowNameColumn(value: boolean) {
  showNameColumn.value = value;
  writeColumnPrefs(dataSourceId.value);
}

function onShowDisplayNameColumn(value: boolean) {
  showDisplayNameColumn.value = value;
  writeColumnPrefs(dataSourceId.value);
}

function onShowAddressColumn(value: boolean) {
  showAddressColumn.value = value;
  writeColumnPrefs(dataSourceId.value);
}

function onShowCommentColumn(value: boolean) {
  showCommentColumn.value = value;
  writeColumnPrefs(dataSourceId.value);
}

function onShowCycleColumn(value: boolean) {
  showCycleColumn.value = value;
  writeColumnPrefs(dataSourceId.value);
}

function onShowActiveColumn(value: boolean) {
  showActiveColumn.value = value;
  writeColumnPrefs(dataSourceId.value);
}

watch(dataSourceId, (id) => {
  const prefs = readColumnPrefs(id);
  showIdColumn.value = prefs.id;
  showNameColumn.value = prefs.name;
  showDisplayNameColumn.value = prefs.displayName;
  showAddressColumn.value = prefs.address;
  showCommentColumn.value = prefs.comment;
  showCycleColumn.value = prefs.cycle;
  showActiveColumn.value = prefs.active;
  sorts.value = [];
  sourceName.value = "";
  sourceState.value = "";
  sourceType.value = "";
  error.value = "";
  savedTopIndex.clear();
  restoreTopIndex = null;
  resetBrowseHeaderState();
  resetXmlImportHeaderState();
  closeBrowse();
  closeXmlImport();
  reloadList();
}, { immediate: true });

function ignoresArrowSelection(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }
  if (target.closest("button, textarea, select, a")) {
    return true;
  }
  if (target instanceof HTMLInputElement && target.type !== "checkbox") {
    return true;
  }
  return target.isContentEditable;
}

function onKey(event: KeyboardEvent) {
  if (event.key === "ArrowUp" || event.key === "ArrowDown") {
    if (event.altKey || event.ctrlKey || event.metaKey || ignoresArrowSelection(event.target)) {
      return;
    }
    if (selected.value.size === 0) {
      return;
    }
    event.preventDefault();
    onNudge(event.key === "ArrowDown" ? 1 : -1);
    return;
  }
  if (event.key !== "Escape") {
    return;
  }
  if (tagContextMenu.value) {
    closeTagContextMenu();
    return;
  }
  if (browseOpen.value && !browseRunning.value) {
    closeBrowse();
    return;
  }
  if (xmlImportOpen.value && !xmlImportActive.value) {
    closeXmlImport();
    return;
  }
  if (cycleOpen.value) {
    cycleOpen.value = false;
  }
}

onMounted(() => {
  stopped = false;
  window.addEventListener("keydown", onKey);
  schedulePoll();
});

onUnmounted(() => {
  stopped = true;
  window.removeEventListener("keydown", onKey);
  clearDragSelect();
  if (debounceTimer !== undefined) {
    window.clearTimeout(debounceTimer);
  }
  if (pollTimer !== undefined) {
    window.clearTimeout(pollTimer);
  }
  abortPageLoads();
  valueSession.abort();
  actionController?.abort();
  browseController?.abort();
  stopBrowsePoll();
  stopBrowseUiTick();
  xmlImportController?.abort();
  xmlUploadController?.abort();
  stopXmlImportPoll();
});
</script>
