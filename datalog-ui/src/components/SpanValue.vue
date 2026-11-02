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
  <span
    class="chart-span-value"
    :class="{ 'is-open': open }"
    @mouseenter="onEnter"
    @mouseleave="onLeave"
  >
    {{ formatSpan(seconds) }}
    <span ref="tipRef" class="chart-measure-tip" @mouseenter="clearCloseTimer" @mouseleave="onTipLeave">{{ tip }}</span>
  </span>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import { TIP_CLOSE_DELAY_MS, TIP_OPEN_DELAY_MS } from "../chart/measureCursors";
import { formatSpan, formatSpanLines } from "../format";

const props = defineProps<{
  /** The time span in seconds. */
  seconds: number;
}>();

const open = ref(false);
const tipRef = ref<HTMLElement | null>(null);
let openTimer = 0;
let closeTimer = 0;

// The span in seconds, minutes, hours and days, one below the other, as in the tooltip of the badge dX
const tip = computed(() => formatSpanLines(props.seconds).join("\n"));

function clearCloseTimer() {
  window.clearTimeout(closeTimer);
}

function isTipSelected(): boolean {
  const selection = window.getSelection();
  const element = tipRef.value;
  if (!selection || !element) {
    return false;
  }
  for (let i = 0; i < selection.rangeCount; i++) {
    if (selection.getRangeAt(i).intersectsNode(element)) {
      return true;
    }
  }
  return false;
}

// Closes the tooltip and ends a selection in it
function closeTip() {
  window.clearTimeout(openTimer);
  window.clearTimeout(closeTimer);
  const selected = isTipSelected();
  if (!selected && !open.value) {
    return;
  }
  open.value = false;
  if (selected) {
    window.getSelection()?.removeAllRanges();
  }
}

// The tooltip opens when the mouse rested on the value and closes 1 s after the mouse left it, so the mouse can move
// to the tooltip; it stays open while the mouse is over it and closes at once when the mouse leaves it
function onEnter() {
  window.clearTimeout(closeTimer);
  if (!open.value) {
    window.clearTimeout(openTimer);
    openTimer = window.setTimeout(() => (open.value = true), TIP_OPEN_DELAY_MS);
  }
}

function onLeave() {
  window.clearTimeout(closeTimer);
  window.clearTimeout(openTimer);
  if (open.value) {
    closeTimer = window.setTimeout(closeTip, TIP_CLOSE_DELAY_MS);
  }
}

function onTipLeave(event: MouseEvent) {
  if ((event.buttons & 1) === 0) {
    closeTip();
    return;
  }
  // The text is being selected by dragging: the tooltip closes when the button is released
  const onUp = () => {
    document.removeEventListener("mouseup", onUp);
    if (!tipRef.value?.matches(":hover")) {
      closeTip();
    }
  };
  document.addEventListener("mouseup", onUp);
}

onBeforeUnmount(() => {
  window.clearTimeout(openTimer);
  window.clearTimeout(closeTimer);
});
</script>
