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

<!-- A bare trend line with a light area below, in the colour of its parent (currentColor) -->
<template>
  <svg class="trend-sparkline" viewBox="0 0 100 24" preserveAspectRatio="none" aria-hidden="true">
    <path v-if="geometry.area" :d="geometry.area" fill="currentColor" fill-opacity="0.08" stroke="none" />
    <polyline
      v-if="geometry.line"
      :points="geometry.line"
      fill="none"
      stroke="currentColor"
      stroke-width="1.5"
      stroke-linecap="round"
      stroke-linejoin="round"
      vector-effect="non-scaling-stroke"
    />
    <line
      v-else
      x1="0"
      y1="23"
      x2="100"
      y2="23"
      stroke="currentColor"
      stroke-opacity="0.2"
      vector-effect="non-scaling-stroke"
    />
  </svg>
</template>

<script setup lang="ts">
import { computed } from "vue";

const props = defineProps<{
  values: number[];
  // A fixed top of the scale, like 100 for a percentage; else the largest value
  max?: number;
}>();

const geometry = computed(() => {
  const values = props.values;
  if (values.length < 2) {
    return { line: "", area: "" };
  }

  // Some room above the largest value, so a steady rate is no line at the top edge
  const top = props.max ?? Math.max(...values, 0) * 1.25;
  const scale = top > 0 ? top : 1;
  const last = values.length - 1;
  const points = values.map((value, index) => {
    const x = (index / last) * 100;
    const y = 23 - (Math.min(Math.max(value, 0), scale) / scale) * 21;
    return `${x.toFixed(2)},${y.toFixed(2)}`;
  });
  return {
    line: points.join(" "),
    area: `M 0 23 L ${points.join(" L ").replaceAll(",", " ")} L 100 23 Z`
  };
});
</script>
