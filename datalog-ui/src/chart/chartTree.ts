/*
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
*/

import type { ChartTreeItem } from "../types";

export const ROOT_LABEL = "Charts";

export type ChartTreeAction = "open" | "edit" | "rename" | "move" | "sort" | "delete" | "new-chart" | "new-folder";

export interface ChartTreeNode {
  item: ChartTreeItem;
  children: ChartTreeNode[];
  depth: number;
}

export interface ChartTreeIndex {
  root: ChartTreeNode | null;
  byId: Map<number, ChartTreeNode>;
}

export function isContainer(item: ChartTreeItem): boolean {
  return item.type === "Root" || item.type === "Folder";
}

export function nodeLabel(item: ChartTreeItem): string {
  return item.type === "Root" ? ROOT_LABEL : item.name;
}

/** The tree below the root; the children of a folder are in the order the user gave them. Nodes without a known parent go to the root. */
export function buildTree(items: ChartTreeItem[]): ChartTreeIndex {
  const byId = new Map<number, ChartTreeNode>();
  for (const item of items) {
    byId.set(item.id, { item, children: [], depth: 0 });
  }
  const root = [...byId.values()].find((node) => node.item.type === "Root") ?? null;
  if (!root) {
    return { root: null, byId };
  }

  for (const node of byId.values()) {
    if (node === root) {
      continue;
    }
    const parent = byId.get(node.item.parentId);
    (parent && parent !== node && isContainer(parent.item) ? parent : root).children.push(node);
  }

  const visit = (node: ChartTreeNode, depth: number, seen: Set<number>) => {
    node.depth = depth;
    seen.add(node.item.id);
    node.children = node.children.filter((child) => !seen.has(child.item.id));
    node.children.sort((a, b) => a.item.sortOrder - b.item.sortOrder || a.item.id - b.item.id);
    for (const child of node.children) {
      visit(child, depth + 1, seen);
    }
  };
  visit(root, 0, new Set());
  return { root, byId };
}

/** The rows to show: the root and the children of every expanded folder. */
export function flattenVisible(root: ChartTreeNode | null, expanded: ReadonlySet<number>): ChartTreeNode[] {
  const rows: ChartTreeNode[] = [];
  const walk = (node: ChartTreeNode) => {
    rows.push(node);
    if (node.item.type === "Root" || expanded.has(node.item.id)) {
      for (const child of node.children) {
        walk(child);
      }
    }
  };
  if (root) {
    walk(root);
  }
  return rows;
}

export function descendantIds(node: ChartTreeNode): Set<number> {
  const ids = new Set<number>();
  const walk = (current: ChartTreeNode) => {
    for (const child of current.children) {
      ids.add(child.item.id);
      walk(child);
    }
  };
  walk(node);
  return ids;
}

/** A node can go into another folder or the root, but not into itself, its subtree or where it is. */
export function canMoveTo(index: ChartTreeIndex, nodeId: number, targetId: number): boolean {
  const node = index.byId.get(nodeId);
  const target = index.byId.get(targetId);
  if (!node || !target || node.item.type === "Root" || !isContainer(target.item)) {
    return false;
  }
  if (nodeId === targetId || node.item.parentId === targetId) {
    return false;
  }
  return !descendantIds(node).has(targetId);
}

/** Where a dragged node would go: below a folder, before one of its children (null: last). */
export interface DropPlace {
  parentId: number;
  beforeId: number | null;
}

/**
 * The place for a drop on a row: into a folder (last), or before or after the row among its siblings. Null where a drop
 * makes no sense: the node into itself or its subtree, onto the root before or after, or to where it already is.
 */
export function dropPlace(
  index: ChartTreeIndex,
  nodeId: number,
  targetId: number,
  position: "before" | "after" | "into"
): DropPlace | null {
  const node = index.byId.get(nodeId);
  const target = index.byId.get(targetId);
  if (!node || !target || node.item.type === "Root" || nodeId === targetId) {
    return null;
  }

  let parent: ChartTreeNode | undefined;
  let beforeId: number | null = null;
  if (position === "into") {
    parent = target;
  } else {
    parent = index.byId.get(target.item.parentId);
    if (target.item.type === "Root" || !parent) {
      return null;
    }
    const siblings = parent.children;
    const at = siblings.indexOf(target);
    beforeId = position === "before" ? target.item.id : (siblings[at + 1]?.item.id ?? null);
  }
  if (!parent || !isContainer(parent.item) || parent === node || descendantIds(node).has(parent.item.id)) {
    return null;
  }

  // Where it already is: in the same folder before its next sibling, or last when it is last
  if (parent.item.id === node.item.parentId) {
    const siblings = parent.children;
    const at = siblings.indexOf(node);
    if (beforeId === node.item.id || beforeId === (siblings[at + 1]?.item.id ?? null)) {
      return null;
    }
  }
  return { parentId: parent.item.id, beforeId };
}

/** The folders above a node, the root first. */
export function ancestorIds(index: ChartTreeIndex, id: number): number[] {
  const ids: number[] = [];
  const seen = new Set<number>([id]);
  let node = index.byId.get(id);
  while (node && node.item.type !== "Root") {
    const parent = index.byId.get(node.item.parentId);
    if (!parent || seen.has(parent.item.id)) {
      break;
    }
    seen.add(parent.item.id);
    ids.unshift(parent.item.id);
    node = parent;
  }
  return ids;
}

/** For example "Charts / Plant / Line 1". */
export function pathLabel(index: ChartTreeIndex, id: number): string {
  const node = index.byId.get(id);
  if (!node) {
    return "";
  }
  return [...ancestorIds(index, id), id]
    .map((nodeId) => index.byId.get(nodeId))
    .filter((item): item is ChartTreeNode => !!item)
    .map((item) => nodeLabel(item.item))
    .join(" / ");
}

/** The root and all folders in tree order, for a folder picker. */
export function folderOptions(index: ChartTreeIndex): { id: number; label: string; depth: number }[] {
  const options: { id: number; label: string; depth: number }[] = [];
  const walk = (node: ChartTreeNode) => {
    if (!isContainer(node.item)) {
      return;
    }
    options.push({ id: node.item.id, label: nodeLabel(node.item), depth: node.depth });
    for (const child of node.children) {
      walk(child);
    }
  };
  if (index.root) {
    walk(index.root);
  }
  return options;
}

/** The charts in tree order with their path, for a chart picker. */
export function chartOptions(index: ChartTreeIndex): { item: ChartTreeItem; path: string }[] {
  const options: { item: ChartTreeItem; path: string }[] = [];
  const walk = (node: ChartTreeNode) => {
    if (node.item.type === "Chart") {
      options.push({ item: node.item, path: pathLabel(index, node.item.id) });
    }
    for (const child of node.children) {
      walk(child);
    }
  };
  if (index.root) {
    walk(index.root);
  }
  return options;
}
