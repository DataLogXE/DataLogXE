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

export type SortKey = "id" | "name" | "displayName" | "address" | "comment" | "cycle" | "active" | "value" | "status";
export type SortDir = "asc" | "desc";

export interface SortLevel {
  key: SortKey;
  dir: SortDir;
}

export const MAX_SORT_LEVELS = 2;

const SORT_LABELS: Record<SortKey, string> = {
  id: "ID",
  name: "Name",
  displayName: "Display name",
  address: "Address",
  comment: "Comment",
  cycle: "Cycle",
  active: "Active",
  value: "Value",
  status: "Status"
};

export function cycleSort(current: SortLevel[], key: SortKey): SortLevel[] {
  const index = current.findIndex((level) => level.key === key);
  if (index >= 0) {
    if (current[index].dir === "asc") {
      return current.map((level, i) => (i === index ? { key, dir: "desc" } : level));
    }
    return current.filter((_, i) => i !== index);
  }
  if (current.length < MAX_SORT_LEVELS) {
    return [...current, { key, dir: "asc" }];
  }
  return [current[0], { key, dir: "asc" }];
}

export function sortQuery(levels: SortLevel[]): {
  sort1?: SortKey;
  dir1?: SortDir;
  sort2?: SortKey;
  dir2?: SortDir;
} {
  const query: {
    sort1?: SortKey;
    dir1?: SortDir;
    sort2?: SortKey;
    dir2?: SortDir;
  } = {};
  if (levels[0]) {
    query.sort1 = levels[0].key;
    query.dir1 = levels[0].dir;
  }
  if (levels[1]) {
    query.sort2 = levels[1].key;
    query.dir2 = levels[1].dir;
  }
  return query;
}

export function sortLevel(levels: SortLevel[], key: SortKey): number {
  return levels.findIndex((level) => level.key === key) + 1;
}

export function sortDir(levels: SortLevel[], key: SortKey): SortDir | undefined {
  return levels.find((level) => level.key === key)?.dir;
}

export function ariaSort(levels: SortLevel[], key: SortKey): "none" | "ascending" | "descending" {
  const dir = sortDir(levels, key);
  if (dir === "asc") {
    return "ascending";
  }
  if (dir === "desc") {
    return "descending";
  }
  return "none";
}

export function sortTitle(levels: SortLevel[], key: SortKey): string {
  const label = SORT_LABELS[key];
  const dir = sortDir(levels, key);
  const level = sortLevel(levels, key);
  if (!dir) {
    return `Sort by ${label}`;
  }
  const order = dir === "asc" ? "ascending" : "descending";
  return `Sorted by ${label}, ${order} (level ${level})`;
}

export function sortMark(levels: SortLevel[], key: SortKey): string {
  const dir = sortDir(levels, key);
  const level = sortLevel(levels, key);
  if (!dir || level < 1) {
    return "";
  }
  return `${dir === "asc" ? "↑" : "↓"}${level}`;
}
