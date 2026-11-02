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
  <button
    class="btn secondary btn-sm btn-icon chart-export-btn"
    type="button"
    :disabled="busy"
    data-hint="Export as CSV or Excel (key E)"
    aria-label="Export"
    aria-haspopup="dialog"
  >
    <span v-if="busy" class="spinner spinner-sm" aria-hidden="true"></span>
    <svg v-else viewBox="0 0 16 16" aria-hidden="true">
      <path d="M8 2v8M4.5 6.5L8 10l3.5-3.5M3 13h10" />
    </svg>
  </button>
</template>

<script setup lang="ts">
// The button of the chart toolbar that opens the export; it spins while an export runs
defineProps<{
  busy: boolean;
}>();
</script>
