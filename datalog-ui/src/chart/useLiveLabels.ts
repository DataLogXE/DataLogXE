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

import { ref } from "vue";
import { readLiveLabels, writeLiveLabels } from "./chartStorage";
import { isChartShortcut, isLiveLabelsKey } from "./shortcutKeys";

/**
 * The switch for the live labels at the crosshair of a chart, remembered for the next chart. The key ^ (left of 1)
 * toggles it. `inModal` is for a chart that is a dialog itself, where the key must work although a dialog is open.
 */
export function useLiveLabels(options: { inModal?: boolean } = {}) {
  const liveLabels = ref(readLiveLabels());

  function toggleLiveLabels() {
    liveLabels.value = !liveLabels.value;
    writeLiveLabels(liveLabels.value);
  }

  function onKey(event: KeyboardEvent) {
    if (isLiveLabelsKey(event) && isChartShortcut(event, options.inModal)) {
      // Also keeps the dead key from waiting for a letter to put a circumflex on
      event.preventDefault();
      toggleLiveLabels();
    }
  }

  return { liveLabels, toggleLiveLabels, onKey };
}
