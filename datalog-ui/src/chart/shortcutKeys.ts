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

function isTypingTarget(target: EventTarget | null): boolean {
  return target instanceof HTMLElement
    && !!target.closest("input, select, textarea, [contenteditable=''], [contenteditable='true']");
}

/**
 * Whether a key press may trigger a shortcut of the chart: it has no modifier, is not typed into a field and has no
 * dialog above it. `inModal` is for a chart that is a dialog itself.
 */
export function isChartShortcut(event: KeyboardEvent, inModal = false): boolean {
  if (event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || isTypingTarget(event.target)) {
    return false;
  }
  return inModal || !document.querySelector("[aria-modal='true']");
}

/**
 * The key left of 1 on a German keyboard (^), which is a dead key there. The browser reports it as "Dead" and puts
 * the key code of that place: Backquote on a PC keyboard, IntlBackslash on a Mac with an ISO keyboard (where
 * Backquote is the key beside the left Shift). A layout without a dead key there gives "^" itself.
 */
export function isLiveLabelsKey(event: KeyboardEvent): boolean {
  return event.key === "^" || (event.key === "Dead" && (event.code === "Backquote" || event.code === "IntlBackslash"));
}

/** The key E opens the export of the chart, wherever the mouse is. */
export function isExportKey(event: KeyboardEvent, inModal = false): boolean {
  return event.key.toLowerCase() === "e" && isChartShortcut(event, inModal);
}
