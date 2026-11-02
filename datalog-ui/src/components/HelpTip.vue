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
  <!-- A click must not reach a label around it: that would toggle its check box or focus its field -->
  <span
    ref="iconRef"
    class="help-tip"
    tabindex="0"
    role="button"
    :aria-label="`Help: ${text}`"
    :aria-describedby="id"
    @click.prevent.stop
    @mouseenter="show"
    @mouseleave="open = false"
    @focus="show"
    @blur="open = false"
  >
    ?
    <span v-if="open" :id="id" ref="tipRef" class="help-tip-text" role="tooltip" :style="tipStyle">{{ text }}</span>
  </span>
</template>

<script setup lang="ts">
import { nextTick, ref, useId } from "vue";

defineProps<{ text: string }>();

const id = useId();
const open = ref(false);
const iconRef = ref<HTMLElement | null>(null);
const tipRef = ref<HTMLElement | null>(null);
const tipStyle = ref<Record<string, string>>({});

// Fixed below the icon and kept inside the window, so a tip at the right edge neither gets cut nor scrolls the page
async function show() {
  tipStyle.value = { visibility: "hidden" };
  open.value = true;
  await nextTick();
  const icon = iconRef.value?.getBoundingClientRect();
  const tip = tipRef.value?.getBoundingClientRect();
  if (!icon || !tip) {
    return;
  }
  const margin = 8;
  const left = Math.max(margin, Math.min(icon.left - 6, window.innerWidth - tip.width - margin));
  const below = icon.bottom + 6;
  const top = below + tip.height > window.innerHeight - margin ? icon.top - tip.height - 6 : below;
  tipStyle.value = { left: `${left}px`, top: `${top}px` };
}
</script>

<style scoped>
.help-tip {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1rem;
  height: 1rem;
  margin-left: 0.35rem;
  border: 1px solid var(--border);
  border-radius: 50%;
  background: var(--surface);
  color: var(--muted);
  font-size: 0.7rem;
  font-weight: 700;
  line-height: 1;
  /* On the middle of the text of a label or heading */
  vertical-align: 0.12em;
  cursor: help;
  text-transform: none;
  letter-spacing: 0;
}

.help-tip:hover,
.help-tip:focus-visible {
  border-color: var(--accent);
  color: var(--accent);
  outline: none;
}

.help-tip-text {
  position: fixed;
  z-index: 50;
  width: max-content;
  max-width: min(20rem, calc(100vw - 16px));
  padding: 0.4rem 0.6rem;
  border-radius: 6px;
  background: var(--tooltip-bg);
  color: #fff;
  font-size: var(--font-label);
  font-weight: 400;
  line-height: 1.35;
  white-space: normal;
  text-align: left;
  pointer-events: none;
}
</style>
