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
  <div
    v-if="load.level !== 'normal'"
    class="system-busy"
    :class="{ 'is-critical': load.level === 'critical' }"
    role="status"
    aria-live="polite"
  >
    <strong>{{ load.level === "critical" ? "System overloaded" : "System busy" }}</strong>
    <span>{{ effect }}</span>
    <span v-if="load.reason" class="system-busy-reason">{{ load.reason }}</span>
  </div>
</template>

<script setup lang="ts">
// A strip above the page (AppLayout): a statistics or an export that waits tells why here, also over a dialog.
import { computed } from "vue";
import { useSystemLoad } from "../systemLoad";

const { load } = useSystemLoad();

const effect = computed(() => load.value.level === "critical"
  ? "The recording of values comes first: medium and large queries wait, a long one may be stopped."
  : "The recording of values comes first: large queries wait.");
</script>
