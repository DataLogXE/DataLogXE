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
  <div class="overlay chart-dialog-overlay" @click.self="emit('close')">
    <div
      class="overlay-card chart-dialog about-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="about-title"
      @keydown.esc.prevent="emit('close')"
    >
      <strong id="about-title">DataLogXE <small>{{ version }}</small></strong>
      <p>A simple and high performance data logger.</p>
      <p class="about-notice">
        Copyright &copy; 2026 datalogxe@gmail.com<br />
        This program is free software: you can redistribute it and/or modify it under the terms of the GNU General
        Public License as published by the Free Software Foundation, either version 3 of the License, or (at your
        option) any later version.<br />
        This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
        implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License
        for more details.
      </p>
      <p class="about-links">
        <a href="/LICENSE.txt" target="_blank" rel="noopener">License text</a>
        <a href="/THIRD-PARTY-NOTICES.txt" target="_blank" rel="noopener">Third-party licenses</a>
        <a :href="SOURCE_URL" target="_blank" rel="noopener">Source code</a>
        <a href="https://www.gnu.org/licenses/" target="_blank" rel="noopener">gnu.org/licenses</a>
      </p>
      <div class="actions chart-dialog-actions">
        <button ref="closeRef" class="btn secondary" type="button" @click="emit('close')">Close</button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
// Name, copyright and the license notice the GPL asks a program with a user interface to show
import { onMounted, ref } from "vue";

defineProps<{ version: string }>();
const emit = defineEmits<{ close: [] }>();

const SOURCE_URL = "https://github.com/DataLogXE/datalogxe";
const closeRef = ref<HTMLButtonElement | null>(null);

onMounted(() => closeRef.value?.focus());
</script>
