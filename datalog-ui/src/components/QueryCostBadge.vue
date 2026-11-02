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
  <span class="query-activity-cost" :class="`is-${cost}`" :data-hint="HINTS[cost]">{{ cost }}</span>
</template>

<script setup lang="ts">
// The cost class of a query job, the same for the own queries and those of other clients.
import type { QueryCost } from "../types";

defineProps<{ cost: QueryCost }>();

const HINTS: Record<QueryCost, string> = {
  small: "Small query: never waits behind the long ones",
  medium: "Medium query: waits while the system is overloaded",
  large: "Large query: runs one or two at a time and waits while the system is busy"
};
</script>
