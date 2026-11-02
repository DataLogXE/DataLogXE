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
    v-if="state"
    class="alarm-state-icon"
    :class="stateClass"
    :data-hint="label"
    :aria-label="label"
    role="img"
  >
    <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
      <path v-if="base === 'Incoming' || base === 'ActiveUnacked'" d="M8 1.5 14.5 13H1.5L8 1.5Z" fill="currentColor" />
      <path
        v-else-if="hollowTriangle"
        d="M8 1.5 14.5 13H1.5L8 1.5Z"
        fill="none"
        stroke="currentColor"
        stroke-width="1.4"
      />
      <circle v-else-if="base === 'Incoming/Outgoing' || base === 'InactiveUnacked'" cx="8" cy="8" r="6.5" fill="currentColor" />
      <circle
        v-else
        cx="8"
        cy="8"
        r="6.5"
        fill="none"
        stroke="currentColor"
        stroke-width="1.4"
      />
      <path
        v-if="base === 'ActiveAcked' || base === 'Inactive'"
        d="M5 8.2 7.1 10.3 11.2 6"
        fill="none"
        stroke="currentColor"
        stroke-width="1.4"
        stroke-linecap="round"
        stroke-linejoin="round"
      />
    </svg>
  </span>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { alarmStateBase, formatAlarmState } from "../format";

const props = defineProps<{
  state?: string;
  limit?: string;
}>();

const base = computed(() => alarmStateBase(props.state));

const hollowTriangle = computed(() =>
  base.value === "ActiveAcked"
  || base.value === "Suppressed"
  || base.value === "Shelved"
  || base.value === "SuppressedOrShelved"
);

const label = computed(() => formatAlarmState(props.state, props.limit));

const stateClass = computed(() => {
  switch (base.value) {
    case "Incoming":
    case "ActiveUnacked":
    case "ActiveAcked":
      return "alarm-active-unacked";
    case "Incoming/Outgoing":
    case "InactiveUnacked":
      return "alarm-inactive-unacked";
    case "Removed":
    case "Normal":
      return "alarm-inactive";
    case "Disabled":
    case "OutOfService":
      return "alarm-disabled";
    case "Suppressed":
    case "Shelved":
    case "SuppressedOrShelved":
      return "alarm-shelved";
    case "Inactive":
      return "alarm-inactive";
    default:
      return "alarm-inactive";
  }
});
</script>
