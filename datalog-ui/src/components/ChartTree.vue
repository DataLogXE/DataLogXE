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
  <div class="chart-tree" role="tree" aria-label="Charts">
    <div
      v-for="node in rows"
      :key="node.item.id"
      class="chart-tree-row"
      :class="{
        'is-root': node.item.type === 'Root',
        'is-selected': node.item.id === selectedId,
        'is-drop': drop?.id === node.item.id && drop.position === 'into',
        'is-drop-before': drop?.id === node.item.id && drop.position === 'before',
        'is-drop-after': drop?.id === node.item.id && drop.position === 'after',
        'is-dragged': node.item.id === dragId
      }"
      role="treeitem"
      tabindex="0"
      :aria-level="node.depth + 1"
      :aria-selected="node.item.id === selectedId"
      :aria-expanded="isFolder(node) ? isExpanded(node) : undefined"
      :style="{ paddingLeft: `${0.35 + node.depth * 0.95}rem` }"
      :draggable="!readonly && node.item.type !== 'Root'"
      :data-hint="node.item.type === 'Chart' ? `${node.item.name} · ${plotCountLabel(node.item.plotCount)}` : undefined"
      @click="onClick(node)"
      @dblclick="onDblClick(node)"
      @keydown="onKeydown($event, node)"
      @contextmenu.prevent="openMenu(node, $event.clientX, $event.clientY)"
      @dragstart="onDragStart($event, node)"
      @dragend="onDragEnd"
      @dragover="onDragOver($event, node)"
      @dragleave="onDragLeave(node)"
      @drop="onDrop($event)"
    >
      <span
        class="chart-tree-chevron"
        :class="{ 'is-open': isExpanded(node), 'is-hidden': !isFolder(node) || node.children.length === 0 }"
        aria-hidden="true"
        @click.stop="toggle(node)"
      >
        <svg viewBox="0 0 16 16"><path d="M6 4l4 4-4 4" /></svg>
      </span>
      <span class="chart-tree-icon" aria-hidden="true">
        <svg v-if="node.item.type === 'Chart'" viewBox="0 0 24 24">
          <path d="M4 19h16" />
          <path d="M5 15l4-5 4 3 6-7" />
        </svg>
        <svg v-else viewBox="0 0 24 24">
          <path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2Z" />
        </svg>
      </span>
      <span class="chart-tree-name">{{ nodeLabel(node.item) }}</span>
      <span v-if="node.item.type === 'Chart'" class="chart-tree-count">{{ node.item.plotCount }}</span>
      <button
        v-if="!readonly"
        class="chart-tree-menu-btn"
        type="button"
        :aria-label="`Actions for ${nodeLabel(node.item)}`"
        data-hint="Actions"
        @click.stop="openMenuAt(node, $event)"
        @dblclick.stop
      >
        <svg viewBox="0 0 16 16" aria-hidden="true">
          <circle cx="3.5" cy="8" r="1.2" />
          <circle cx="8" cy="8" r="1.2" />
          <circle cx="12.5" cy="8" r="1.2" />
        </svg>
      </button>
    </div>
  </div>

  <Teleport to="body">
    <div
      v-if="menu"
      class="tag-context-menu-backdrop"
      @click="closeMenu"
      @contextmenu.prevent="closeMenu"
    >
      <div
        ref="menuRef"
        class="tag-context-menu"
        role="menu"
        :style="{ left: `${menu.x}px`, top: `${menu.y}px` }"
        @click.stop
        @keydown.esc.prevent="closeMenu"
      >
        <template v-if="menu.node.item.type === 'Chart'">
          <button type="button" role="menuitem" @click="act('open')">Open</button>
          <button type="button" role="menuitem" @click="act('edit')">Edit chart</button>
          <button type="button" role="menuitem" @click="act('rename')">Rename</button>
          <button type="button" role="menuitem" @click="act('move')">Move to …</button>
          <button type="button" role="menuitem" @click="act('delete')">Delete</button>
        </template>
        <template v-else>
          <button type="button" role="menuitem" @click="act('new-chart')">New chart</button>
          <button type="button" role="menuitem" @click="act('new-folder')">New folder</button>
          <button type="button" role="menuitem" :disabled="menu.node.children.length < 2" @click="act('sort')">Sort</button>
          <template v-if="menu.node.item.type === 'Folder'">
            <button type="button" role="menuitem" @click="act('rename')">Rename</button>
            <button type="button" role="menuitem" @click="act('move')">Move to …</button>
            <button type="button" role="menuitem" @click="act('delete')">Delete</button>
          </template>
        </template>
      </div>
    </div>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, nextTick, ref } from "vue";
import {
  dropPlace,
  flattenVisible,
  nodeLabel,
  type ChartTreeAction,
  type ChartTreeIndex,
  type ChartTreeNode
} from "../chart/chartTree";
import type { ChartTreeItem } from "../types";

const props = defineProps<{
  index: ChartTreeIndex;
  selectedId: number | null;
  expanded: ReadonlySet<number>;
  /** A viewer only selects charts: no menu, no editing, no drag and drop. */
  readonly?: boolean;
}>();

const emit = defineEmits<{
  select: [id: number];
  toggle: [id: number];
  action: [action: ChartTreeAction, item: ChartTreeItem];
  /** A node was dropped: below the folder, before its child `beforeId`, or last when that is null. */
  reorder: [id: number, parentId: number, beforeId: number | null];
}>();

const MENU_WIDTH = 176;
const MENU_HEIGHT = 220;

const menu = ref<{ node: ChartTreeNode; x: number; y: number } | null>(null);
const menuRef = ref<HTMLElement | null>(null);
const dragId = ref<number | null>(null);
// Where a drop would go: into a folder, or before or after a row
const drop = ref<{ id: number; position: "before" | "after" | "into"; parentId: number; beforeId: number | null } | null>(null);

const rows = computed(() => flattenVisible(props.index.root, props.expanded));

function isFolder(node: ChartTreeNode): boolean {
  return node.item.type === "Folder";
}

function isExpanded(node: ChartTreeNode): boolean {
  return node.item.type === "Root" || props.expanded.has(node.item.id);
}

function plotCountLabel(count: number): string {
  return count === 1 ? "1 data tag" : `${count} data tags`;
}

function toggle(node: ChartTreeNode) {
  if (isFolder(node)) {
    emit("toggle", node.item.id);
  }
}

function onClick(node: ChartTreeNode) {
  if (node.item.type === "Chart") {
    emit("select", node.item.id);
  } else {
    toggle(node);
  }
}

function onDblClick(node: ChartTreeNode) {
  if (!props.readonly && node.item.type === "Chart") {
    emit("action", "edit", node.item);
  }
}

function onKeydown(event: KeyboardEvent, node: ChartTreeNode) {
  if (event.target !== event.currentTarget) {
    return;
  }
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    onClick(node);
    return;
  }
  if (event.key === "ArrowRight" && isFolder(node) && !isExpanded(node)) {
    event.preventDefault();
    toggle(node);
    return;
  }
  if (event.key === "ArrowLeft" && isFolder(node) && isExpanded(node)) {
    event.preventDefault();
    toggle(node);
    return;
  }
  if (event.key === "ArrowDown" || event.key === "ArrowUp") {
    event.preventDefault();
    const row = event.currentTarget as HTMLElement;
    const next = (event.key === "ArrowDown" ? row.nextElementSibling : row.previousElementSibling) as HTMLElement | null;
    next?.focus();
  }
}

function openMenu(node: ChartTreeNode, clientX: number, clientY: number) {
  if (props.readonly) {
    return;
  }
  menu.value = {
    node,
    x: Math.max(4, Math.min(clientX, window.innerWidth - MENU_WIDTH - 4)),
    y: Math.max(4, Math.min(clientY, window.innerHeight - MENU_HEIGHT - 4))
  };
  void nextTick(() => menuRef.value?.querySelector<HTMLButtonElement>("button")?.focus());
}

function openMenuAt(node: ChartTreeNode, event: MouseEvent) {
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
  openMenu(node, rect.left, rect.bottom + 2);
}

function closeMenu() {
  menu.value = null;
}

function act(action: ChartTreeAction) {
  const node = menu.value?.node;
  closeMenu();
  if (node) {
    emit("action", action, node.item);
  }
}

function onDragStart(event: DragEvent, node: ChartTreeNode) {
  if (props.readonly || node.item.type === "Root" || !event.dataTransfer) {
    event.preventDefault();
    return;
  }
  dragId.value = node.item.id;
  event.dataTransfer.effectAllowed = "move";
  event.dataTransfer.setData("text/plain", String(node.item.id));
}

function onDragEnd() {
  dragId.value = null;
  drop.value = null;
}

// A folder takes a drop on its middle half into itself, on the upper quarter before it and on the lower quarter after
// it; a chart takes the upper half before and the lower half after. The root only takes a node into itself.
function positionOf(event: DragEvent, node: ChartTreeNode): "before" | "after" | "into" {
  if (node.item.type === "Root") {
    return "into";
  }
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
  const share = rect.height > 0 ? (event.clientY - rect.top) / rect.height : 0.5;
  if (node.item.type === "Folder") {
    return share < 0.25 ? "before" : share > 0.75 ? "after" : "into";
  }
  return share < 0.5 ? "before" : "after";
}

function onDragOver(event: DragEvent, node: ChartTreeNode) {
  if (dragId.value == null) {
    return;
  }
  const position = positionOf(event, node);
  const place = dropPlace(props.index, dragId.value, node.item.id, position);
  if (!place) {
    drop.value = null;
    return;
  }
  event.preventDefault();
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = "move";
  }
  drop.value = { id: node.item.id, position, ...place };
}

function onDragLeave(node: ChartTreeNode) {
  if (drop.value?.id === node.item.id) {
    drop.value = null;
  }
}

function onDrop(event: DragEvent) {
  const id = dragId.value;
  const place = drop.value;
  onDragEnd();
  if (id == null || !place) {
    return;
  }
  event.preventDefault();
  emit("reorder", id, place.parentId, place.beforeId);
}
</script>
