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
  <div v-if="visible" class="overlay">
    <div class="overlay-card">
      <div style="display:flex; gap:0.75rem; align-items:center;">
        <div v-if="!error" class="spinner"></div>
        <strong>{{ error ? "Error" : title }}</strong>
      </div>
      <div :class="error ? 'error-text' : 'hint'">{{ error || message }}</div>
      <button v-if="cancellable" class="btn secondary" type="button" @click="$emit('cancel')">
        Cancel
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
defineProps<{
  visible: boolean;
  title?: string;
  message?: string;
  error?: string;
  cancellable?: boolean;
}>();

defineEmits<{
  cancel: [];
}>();
</script>
