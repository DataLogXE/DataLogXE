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

export const MAX_SELECTED = 1000;

export type SelectMode = "replace" | "toggle" | "range";

export interface TagSelectEvent {
  index: number;
  mode: SelectMode;
  dragging: boolean;
}

export interface SelectableRow {
  id: number;
  isSys: boolean;
}

export function clampIndex(index: number, total: number): number {
  if (total <= 0) {
    return 0;
  }
  if (index < 0) {
    return 0;
  }
  if (index >= total) {
    return total - 1;
  }
  return index;
}

export function indexFromPointer(
  clientY: number,
  bodyTop: number,
  scrollTop: number,
  rowHeight: number,
  total: number
): number {
  return clampIndex(Math.floor((clientY - bodyTop + scrollTop) / rowHeight), total);
}

export function cappedRange(anchor: number, focus: number, total: number): { from: number; to: number } {
  if (total <= 0) {
    return { from: 0, to: -1 };
  }
  const a = clampIndex(anchor, total);
  const f = clampIndex(focus, total);
  if (f >= a) {
    return { from: a, to: Math.min(f, a + MAX_SELECTED - 1) };
  }
  return { from: Math.max(f, a - (MAX_SELECTED - 1)), to: a };
}

export function idsInIndexRange(
  getRow: (index: number) => SelectableRow | undefined,
  from: number,
  to: number
): number[] {
  const ids: number[] = [];
  for (let i = from; i <= to; i++) {
    const row = getRow(i);
    if (row && !row.isSys) {
      ids.push(row.id);
    }
  }
  return ids;
}

export function applyClick(options: {
  mode: SelectMode;
  index: number;
  anchor: number;
  total: number;
  selected: Set<number>;
  getRow: (index: number) => SelectableRow | undefined;
}): { selected: Set<number>; anchor: number } {
  const row = options.getRow(options.index);

  if (options.mode === "range") {
    const anchor = options.anchor >= 0 ? options.anchor : options.index;
    const { from, to } = cappedRange(anchor, options.index, options.total);
    return {
      selected: new Set(idsInIndexRange(options.getRow, from, to)),
      anchor
    };
  }

  if (!row || row.isSys) {
    return { selected: options.selected, anchor: options.anchor };
  }

  if (options.mode === "toggle") {
    const next = new Set(options.selected);
    if (next.has(row.id)) {
      next.delete(row.id);
    } else if (next.size < MAX_SELECTED) {
      next.add(row.id);
    }
    return { selected: next, anchor: options.index };
  }

  return { selected: new Set([row.id]), anchor: options.index };
}
