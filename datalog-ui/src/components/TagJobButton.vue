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
    class="btn secondary tag-job-btn"
    type="button"
    :aria-busy="state === 'running' || undefined"
    :data-hint="state === 'error' ? error : undefined"
  >
    <span class="tag-job-btn-indicator" aria-hidden="true">
      <span v-if="state === 'running'" class="spinner spinner-sm tag-job-spinner"></span>
      <span v-else-if="state === 'error'" class="tag-job-btn-error">
        <svg viewBox="0 0 16 16">
          <rect width="16" height="16" rx="3" />
          <path d="M5.2 5.2l5.6 5.6M10.8 5.2l-5.6 5.6" />
        </svg>
      </span>
    </span>
    <span class="tag-job-btn-label">{{ label }}</span>
  </button>
</template>

<script setup lang="ts">
// The header button that opens a tag job dialog: a spinner while the job runs, a red mark after an error. The
// indicator has a twin of the same width on the right, so the label stays centered.
defineProps<{
  label: string;
  state: "idle" | "running" | "error";
  error: string;
}>();
</script>
