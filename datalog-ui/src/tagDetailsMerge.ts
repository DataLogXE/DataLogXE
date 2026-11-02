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

// The details of several data tags in one dialog: a property with the same value in all tags shows it, else it is mixed (<>).

export const MIXED_TEXT = "<>";

export type MergedValue<T> = { mixed: false; value: T } | { mixed: true };

export function mergeValue<R, T>(rows: readonly R[], pick: (row: R) => T): MergedValue<T> | null {
  if (rows.length === 0) {
    return null;
  }
  const value = pick(rows[0]);
  for (let i = 1; i < rows.length; i++) {
    if (pick(rows[i]) !== value) {
      return { mixed: true };
    }
  }
  return { mixed: false, value };
}

export function mergedText<T>(merged: MergedValue<T> | null, format: (value: T) => string = String): string {
  if (!merged) {
    return "";
  }
  return merged.mixed ? MIXED_TEXT : format(merged.value);
}

/**
 * The value shown in an editable field: the edit when there is one, else the common value, null while mixed.
 */
export function shownValue<T>(merged: MergedValue<T> | null, edit: T | null): T | null {
  if (edit !== null) {
    return edit;
  }
  return merged && !merged.mixed ? merged.value : null;
}

/** A new value equal to the common value of all tags is no change. */
export function editFor<T>(merged: MergedValue<T> | null, value: T): T | null {
  return merged && !merged.mixed && merged.value === value ? null : value;
}
