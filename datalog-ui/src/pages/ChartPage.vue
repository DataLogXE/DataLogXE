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
  <div class="chart-page" :class="{ 'is-tree-hidden': !treeShown, 'is-compact': compact }">
    <div class="page-header">
      <button
        class="icon-btn chart-tree-toggle"
        type="button"
        :data-hint="treeShown ? 'Hide the chart tree' : 'Show the chart tree'"
        :aria-label="treeShown ? 'Hide the chart tree' : 'Show the chart tree'"
        :aria-expanded="treeShown"
        @click="toggleTree"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <rect x="3.5" y="4.5" width="17" height="15" rx="2" />
          <path d="M9.5 4.5v15" />
        </svg>
      </button>
      <!-- Beside the switch of the tree and before the name, like the switch -->
      <button
        v-if="chart && isAdmin"
        class="icon-btn"
        type="button"
        data-hint="Edit chart"
        aria-label="Edit chart"
        @click="openEditor(chart.id, chart.parentId)"
      >
        <svg viewBox="0 0 16 16" aria-hidden="true">
          <path d="M10.5 3l2.5 2.5L5.5 13 2.5 13.5 3 10.5z" />
        </svg>
      </button>
      <h1 :data-hint="title">{{ title }}</h1>
    </div>

    <div class="chart-page-body">
      <div v-if="compact && drawerOpen" class="chart-drawer-backdrop" @click="drawerOpen = false"></div>
      <aside v-show="treeShown" class="chart-tree-pane" aria-label="Chart tree">
        <div v-if="compact" class="chart-drawer-head">
          <strong>Charts</strong>
          <button class="chart-icon-btn" type="button" data-hint="Close" aria-label="Close the chart tree" @click="drawerOpen = false">
            <svg viewBox="0 0 16 16" aria-hidden="true">
              <path d="M4 4l8 8M12 4L4 12" />
            </svg>
          </button>
        </div>
        <div v-if="treeError" class="chart-tree-error">
          <p class="error-text">{{ treeError }}</p>
          <button class="btn secondary btn-sm" type="button" @click="loadTree">Retry</button>
        </div>
        <ChartTree
          v-else-if="treeIndex.root"
          :index="treeIndex"
          :selected-id="selectedId"
          :expanded="expanded"
          :readonly="!isAdmin"
          @select="selectChart"
          @toggle="toggleFolder"
          @action="onAction"
          @reorder="reorderItem"
        />
        <p v-else class="hint chart-tree-loading">Loading …</p>
      </aside>

      <section class="chart-main">
        <ChartStack
          v-if="chart && chart.plots.length"
          :plots="chart.plots"
          :chart-name="chart.name"
          :load-id="loadId"
          @toggle-tree="onPlotDblClick"
        />
        <div v-else class="chart-empty">
          <template v-if="chartLoading">
            <p class="hint">Loading …</p>
          </template>
          <template v-else-if="chartError">
            <p class="error-text">{{ chartError }}</p>
          </template>
          <template v-else-if="chart">
            <p class="hint">This chart has no data tags.</p>
            <button v-if="isAdmin" class="btn" type="button" @click="openEditor(chart.id, chart.parentId)">Edit chart</button>
          </template>
          <template v-else-if="tree && !hasCharts">
            <p class="hint">No charts yet. A chart shows data tags below each other on one time axis.</p>
            <div v-if="isAdmin" class="actions">
              <button class="btn" type="button" @click="openEditor(null, rootId)">New chart</button>
              <button class="btn secondary" type="button" @click="openNameDialog('new-folder', rootItem)">New folder</button>
            </div>
          </template>
          <template v-else-if="tree">
            <p class="hint">Select a chart in the tree.</p>
          </template>
        </div>
      </section>
    </div>

    <ChartNameDialog
      v-if="nameDialog"
      :title="nameDialog.title"
      :initial="nameDialog.initial"
      :submit-label="nameDialog.submitLabel"
      :busy="dialogBusy"
      :error="dialogError"
      @submit="submitName"
      @close="closeDialogs"
    />

    <ConfirmDialog
      v-if="deleteDialog"
      :title="deleteDialog.type === 'Folder' ? 'Delete folder' : 'Delete chart'"
      :message="deleteDialog.type === 'Folder'
        ? `Delete folder &quot;${deleteDialog.name}&quot; with all its charts and folders?`
        : `Delete chart &quot;${deleteDialog.name}&quot;?`"
      confirm-label="Delete"
      :busy="dialogBusy"
      :error="dialogError"
      @confirm="confirmDelete"
      @close="closeDialogs"
    />

    <ChartMoveDialog
      v-if="moveDialog"
      :index="treeIndex"
      :item-id="moveDialog.id"
      :item-name="moveDialog.name"
      :busy="dialogBusy"
      :error="dialogError"
      @submit="submitMove"
      @close="closeDialogs"
    />

    <ChartEditorDialog
      v-if="editor"
      :chart-id="editor.chartId"
      :parent-id="editor.parentId"
      :max-plots="maxPlots"
      @saved="onEditorSaved"
      @close="editor = null"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import ChartEditorDialog from "../components/ChartEditorDialog.vue";
import ChartMoveDialog from "../components/ChartMoveDialog.vue";
import ChartNameDialog from "../components/ChartNameDialog.vue";
import ChartStack from "../components/ChartStack.vue";
import ChartTree from "../components/ChartTree.vue";
import { isAdmin } from "../session";
import ConfirmDialog from "../components/ConfirmDialog.vue";
import { ApiError, createChart, deleteChart, getChart, getCharts, isAbortError, moveChart, sortChartChildren, updateChart } from "../api";
import {
  readExpandedFolders,
  readLastChartId,
  readTreeVisible,
  writeExpandedFolders,
  writeLastChartId,
  writeTreeVisible
} from "../chart/chartStorage";
import {
  ancestorIds,
  buildTree,
  descendantIds,
  pathLabel,
  ROOT_LABEL,
  type ChartTreeAction
} from "../chart/chartTree";
import { notify } from "../notify";
import type { ChartDetail, ChartTree as ChartTreeData, ChartTreeItem } from "../types";

const COMPACT_QUERY = "(max-width: 720px)";
const DEFAULT_MAX_PLOTS = 16;

type NameDialog = {
  kind: "new-folder" | "rename";
  item: ChartTreeItem;
  title: string;
  initial: string;
  submitLabel: string;
};

const route = useRoute();
const router = useRouter();

const tree = ref<ChartTreeData | null>(null);
const treeError = ref("");
const expanded = ref<Set<number>>(readExpandedFolders());
const chart = ref<ChartDetail | null>(null);
const chartError = ref("");
const chartLoading = ref(false);
const loadId = ref(0);
const treeVisible = ref(readTreeVisible());
const compactQuery = window.matchMedia(COMPACT_QUERY);
const compact = ref(compactQuery.matches);
const drawerOpen = ref(false);
const nameDialog = ref<NameDialog | null>(null);
const moveDialog = ref<ChartTreeItem | null>(null);
const deleteDialog = ref<ChartTreeItem | null>(null);
const editor = ref<{ chartId: number | null; parentId: number } | null>(null);
const dialogBusy = ref(false);
const dialogError = ref("");

let chartAbort: AbortController | null = null;

const treeIndex = computed(() => buildTree(tree.value?.items ?? []));
const rootItem = computed(() => treeIndex.value.root?.item ?? null);
const rootId = computed(() => rootItem.value?.id ?? 1);
const hasCharts = computed(() => (tree.value?.items.length ?? 0) > 1);
const maxPlots = computed(() => tree.value?.maxPlots ?? DEFAULT_MAX_PLOTS);
const treeShown = computed(() => (compact.value ? drawerOpen.value : treeVisible.value));
// The start decides first whether it opens the chart shown last; until then nothing is missing
const startPending = ref(true);
// No chart is drawn: none selected, it failed to load or it has no data tags
const noChartShown = computed(
  () => !startPending.value && tree.value != null && !chartLoading.value && !(chart.value && chart.value.plots.length)
);

const selectedId = computed(() => {
  const value = Number(route.params.id);
  return Number.isInteger(value) && value > 0 ? value : null;
});

const title = computed(() => {
  if (!chart.value) {
    return ROOT_LABEL;
  }
  return pathLabel(treeIndex.value, chart.value.id) || chart.value.name;
});

function errorText(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

function setExpanded(next: Set<number>) {
  expanded.value = next;
  writeExpandedFolders(next);
}

function expandTo(id: number) {
  const ancestors = ancestorIds(treeIndex.value, id).filter((ancestor) => ancestor !== rootId.value);
  if (ancestors.every((ancestor) => expanded.value.has(ancestor))) {
    return;
  }
  setExpanded(new Set([...expanded.value, ...ancestors]));
}

function toggleFolder(id: number) {
  const next = new Set(expanded.value);
  if (next.has(id)) {
    next.delete(id);
  } else {
    next.add(id);
  }
  setExpanded(next);
}

async function loadTree() {
  try {
    tree.value = await getCharts();
    treeError.value = "";
    // Folders that no longer exist leave the stored list
    const folders = new Set(tree.value.items.filter((item) => item.type === "Folder").map((item) => item.id));
    const kept = new Set([...expanded.value].filter((id) => folders.has(id)));
    if (kept.size !== expanded.value.size) {
      setExpanded(kept);
    }
    if (chart.value) {
      expandTo(chart.value.id);
    }
  } catch (err) {
    treeError.value = errorText(err, "The charts could not be loaded.");
  }
}

async function loadChart(id: number | null) {
  chartAbort?.abort();
  chartAbort = null;
  if (id == null) {
    chart.value = null;
    chartError.value = "";
    chartLoading.value = false;
    return;
  }
  const controller = new AbortController();
  chartAbort = controller;
  chartLoading.value = true;
  try {
    const detail = await getChart(id, controller.signal);
    if (controller.signal.aborted) {
      return;
    }
    chart.value = detail;
    chartError.value = "";
    loadId.value += 1;
    writeLastChartId(id);
    expandTo(id);
  } catch (err) {
    if (controller.signal.aborted || isAbortError(err)) {
      return;
    }
    chart.value = null;
    if (err instanceof ApiError && err.status === 404) {
      chartError.value = "The chart was not found.";
      writeLastChartId(null);
    } else {
      chartError.value = errorText(err, "The chart could not be loaded.");
    }
  } finally {
    if (chartAbort === controller) {
      chartLoading.value = false;
    }
  }
}

function selectChart(id: number) {
  if (compact.value) {
    drawerOpen.value = false;
  }
  if (id !== selectedId.value) {
    void router.push(`/charts/${id}`);
  }
}

function toggleTree() {
  if (compact.value) {
    drawerOpen.value = !drawerOpen.value;
    return;
  }
  treeVisible.value = !treeVisible.value;
  writeTreeVisible(treeVisible.value);
}

function onPlotDblClick() {
  if (!compact.value) {
    toggleTree();
  }
}

function openEditor(chartId: number | null, parentId: number) {
  if (compact.value) {
    drawerOpen.value = false;
  }
  editor.value = { chartId, parentId };
}

function openNameDialog(kind: NameDialog["kind"], item: ChartTreeItem | null) {
  if (!item) {
    return;
  }
  dialogError.value = "";
  nameDialog.value = kind === "new-folder"
    ? { kind, item, title: "New folder", initial: "New folder", submitLabel: "Create" }
    : {
        kind,
        item,
        title: item.type === "Folder" ? "Rename folder" : "Rename chart",
        initial: item.name,
        submitLabel: "Rename"
      };
}

function closeDialogs() {
  if (dialogBusy.value) {
    return;
  }
  nameDialog.value = null;
  moveDialog.value = null;
  deleteDialog.value = null;
  dialogError.value = "";
}

function onAction(action: ChartTreeAction, item: ChartTreeItem) {
  switch (action) {
    case "open":
      selectChart(item.id);
      break;
    case "edit":
      openEditor(item.id, item.parentId);
      break;
    case "new-chart":
      openEditor(null, item.id);
      break;
    case "new-folder":
    case "rename":
      openNameDialog(action, item);
      break;
    case "move":
      dialogError.value = "";
      moveDialog.value = item;
      break;
    case "sort":
      void sortFolder(item);
      break;
    case "delete":
      dialogError.value = "";
      deleteDialog.value = item;
      break;
  }
}

async function submitName(name: string) {
  const dialog = nameDialog.value;
  if (!dialog || dialogBusy.value) {
    return;
  }
  dialogBusy.value = true;
  dialogError.value = "";
  try {
    if (dialog.kind === "new-folder") {
      await createChart({ name, type: "Folder", parentId: dialog.item.id });
      if (dialog.item.type === "Folder") {
        setExpanded(new Set([...expanded.value, dialog.item.id]));
      }
    } else {
      await updateChart(dialog.item.id, { name });
      if (chart.value?.id === dialog.item.id) {
        chart.value = { ...chart.value, name };
      }
    }
    nameDialog.value = null;
    await loadTree();
  } catch (err) {
    dialogError.value = errorText(err, "The name could not be saved.");
  } finally {
    dialogBusy.value = false;
  }
}

// A drop in the tree: into a folder or between its children; the folder opens to show the item
async function reorderItem(id: number, parentId: number, beforeId: number | null) {
  try {
    await moveChart(id, parentId, beforeId);
    if (parentId !== rootId.value) {
      setExpanded(new Set([...expanded.value, parentId, ...ancestorIds(treeIndex.value, parentId)]));
    }
    await loadTree();
  } catch (err) {
    notify("error", "Move failed", errorText(err, "The item could not be moved."));
    await loadTree();
  }
}

// The charts and folders directly below a folder by name
async function sortFolder(item: ChartTreeItem) {
  try {
    await sortChartChildren(item.id);
    await loadTree();
  } catch (err) {
    notify("error", "Sort failed", errorText(err, "The folder could not be sorted."));
  }
}

async function submitMove(parentId: number) {
  const item = moveDialog.value;
  if (!item || dialogBusy.value) {
    return;
  }
  dialogBusy.value = true;
  dialogError.value = "";
  try {
    await updateChart(item.id, { parentId });
    moveDialog.value = null;
    if (parentId !== rootId.value) {
      setExpanded(new Set([...expanded.value, parentId, ...ancestorIds(treeIndex.value, parentId)]));
    }
    await loadTree();
  } catch (err) {
    dialogError.value = errorText(err, "The item could not be moved.");
  } finally {
    dialogBusy.value = false;
  }
}

// The question is a dialog of the page: the confirm of the browser is not shown everywhere (an embedded browser
// answers it with no at once), and the delete would silently do nothing
async function confirmDelete() {
  const item = deleteDialog.value;
  if (!item || dialogBusy.value) {
    return;
  }
  const isFolder = item.type === "Folder";
  const node = treeIndex.value.byId.get(item.id);
  const current = selectedId.value;
  const affectsCurrent = current != null
    && (current === item.id || (isFolder && !!node && descendantIds(node).has(current)));
  dialogBusy.value = true;
  dialogError.value = "";
  try {
    await deleteChart(item.id);
    deleteDialog.value = null;
    if (affectsCurrent) {
      writeLastChartId(null);
      await router.push("/charts");
    }
    await loadTree();
    notify("success", "Success", isFolder ? "The folder was deleted." : "The chart was deleted.");
  } catch (err) {
    dialogError.value = errorText(err, "The item could not be deleted.");
  } finally {
    dialogBusy.value = false;
  }
}

async function onEditorSaved(id: number) {
  editor.value = null;
  await loadTree();
  if (selectedId.value === id) {
    await loadChart(id);
  } else {
    await router.push(`/charts/${id}`);
  }
}

function onCompactChange(event: MediaQueryListEvent) {
  compact.value = event.matches;
  drawerOpen.value = false;
}

function onKey(event: KeyboardEvent) {
  if (event.key === "Escape" && compact.value && drawerOpen.value && !document.querySelector("[aria-modal='true']")) {
    drawerOpen.value = false;
  }
}

watch(selectedId, (id) => void loadChart(id), { immediate: true });

// Without a chart the tree opens to pick one; the saved choice of the toggle stays as it is
watch(noChartShown, (empty) => {
  if (!empty) {
    return;
  }
  if (compact.value) {
    drawerOpen.value = true;
  } else {
    treeVisible.value = true;
  }
});

onMounted(async () => {
  compactQuery.addEventListener("change", onCompactChange);
  window.addEventListener("keydown", onKey);
  await loadTree();
  // The nav link opens the chart shown last, as long as it still exists
  if (selectedId.value == null) {
    const last = readLastChartId();
    if (last != null && treeIndex.value.byId.get(last)?.item.type === "Chart") {
      await router.replace(`/charts/${last}`);
    }
  }
  // The chart of the route loads in a watcher, which has started when this resumes
  await nextTick();
  startPending.value = false;
});

onUnmounted(() => {
  compactQuery.removeEventListener("change", onCompactChange);
  window.removeEventListener("keydown", onKey);
  chartAbort?.abort();
});
</script>
