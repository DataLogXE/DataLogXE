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
  <div class="throughput-meter">
    <div class="throughput-meter-chart" role="img" :aria-label="ariaLabel">
      <svg class="throughput-sparkline" viewBox="0 0 100 24" preserveAspectRatio="none">
        <defs>
          <linearGradient :id="gradientId" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stop-color="currentColor" stop-opacity="0.22" />
            <stop offset="100%" stop-color="currentColor" stop-opacity="0.02" />
          </linearGradient>
        </defs>
        <path v-if="areaPath" :d="areaPath" :fill="`url(#${gradientId})`" stroke="none" />
        <polyline
          v-if="linePoints"
          :points="linePoints"
          fill="none"
          stroke="currentColor"
          stroke-width="1.5"
          stroke-linecap="round"
          stroke-linejoin="round"
          vector-effect="non-scaling-stroke"
        />
        <line
          v-if="!linePoints"
          x1="0"
          y1="22"
          x2="100"
          y2="22"
          stroke="currentColor"
          stroke-width="1"
          stroke-opacity="0.2"
          vector-effect="non-scaling-stroke"
        />
        <circle
          v-if="lastDot"
          :cx="lastDot.x"
          :cy="lastDot.y"
          r="2"
          fill="currentColor"
          vector-effect="non-scaling-stroke"
        />
      </svg>
      <div
        class="throughput-meter-value"
        :class="{ 'is-empty': rateText === '–' }"
        :data-hint="rateTitle"
      >
        {{ rateText }}
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { formatNumber } from "../format";

const props = defineProps<{
  values: number[];
  current: number | null | undefined;
}>();

const gradientId = `throughput-fill-${Math.random().toString(36).slice(2, 9)}`;

const resolvedRate = computed(() => {
  if (props.current != null && Number.isFinite(props.current)) {
    return props.current;
  }
  if (props.values.length) {
    const last = props.values[props.values.length - 1];
    return Number.isFinite(last) ? last : null;
  }
  return null;
});

const rateText = computed(() => {
  const rate = resolvedRate.value;
  if (rate == null) {
    return "–";
  }
  const abs = Math.abs(rate);
  if (abs >= 1000) {
    return formatNumber(rate, { maximumFractionDigits: 0 });
  }
  if (abs >= 10) {
    return formatNumber(rate, { maximumFractionDigits: 1 });
  }
  return formatNumber(rate, { maximumFractionDigits: 2 });
});

const rateTitle = computed(() => {
  const rate = resolvedRate.value;
  if (rate == null) {
    return "No rate data yet";
  }
  return `Current values per second: ${formatNumber(rate, { maximumFractionDigits: 6 })}`;
});

const ariaLabel = computed(() => {
  return `Values per second, last 10 minutes. Current: ${rateText.value}.`;
});

const chartGeometry = computed(() => {
  const values = props.values;
  if (!values.length) {
    return { linePoints: "", areaPath: "", lastDot: null as { x: number; y: number } | null };
  }

  const max = Math.max(...values, 0);
  const scaleMax = max > 0 ? max : 1;
  const lastIndex = values.length - 1;
  const baseline = 23;

  const pts = values.map((value, index) => {
    const x = lastIndex === 0 ? 50 : (index / lastIndex) * 100;
    const clamped = Math.max(0, value);
    const y = baseline - (clamped / scaleMax) * 21;
    return { x, y };
  });

  const linePoints = pts.map((p) => `${p.x.toFixed(2)},${p.y.toFixed(2)}`).join(" ");
  const first = pts[0];
  const last = pts[pts.length - 1];
  const areaPath = `M ${first.x.toFixed(2)} ${baseline} L ${pts.map((p) => `${p.x.toFixed(2)} ${p.y.toFixed(2)}`).join(" L ")} L ${last.x.toFixed(2)} ${baseline} Z`;

  return {
    linePoints,
    areaPath,
    lastDot: last
  };
});

const linePoints = computed(() => chartGeometry.value.linePoints);
const areaPath = computed(() => chartGeometry.value.areaPath);
const lastDot = computed(() => chartGeometry.value.lastDot);
</script>
