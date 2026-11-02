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
    <form
      class="overlay-card chart-dialog"
      role="alertdialog"
      aria-modal="true"
      aria-labelledby="confirm-title"
      aria-describedby="confirm-message"
      @submit.prevent="submit"
      @keydown.esc.prevent="emit('close')"
    >
      <strong id="confirm-title">{{ title }}</strong>
      <p id="confirm-message">{{ message }}</p>
      <p v-if="error" class="error-text">{{ error }}</p>
      <div class="actions chart-dialog-actions">
        <button ref="cancelRef" class="btn secondary" type="button" @click="emit('close')">Cancel</button>
        <button class="btn danger" type="submit" :disabled="busy">{{ confirmLabel }}</button>
      </div>
    </form>
  </div>
</template>

<script setup lang="ts">
// A question of the page with a button for the answer. The confirm of the browser is not shown everywhere: an embedded
// browser answers it with no at once, and the action would silently do nothing.
import { onMounted, ref } from "vue";

const props = defineProps<{
  title: string;
  message: string;
  confirmLabel: string;
  busy: boolean;
  error: string;
}>();

const emit = defineEmits<{
  confirm: [];
  close: [];
}>();

const cancelRef = ref<HTMLButtonElement | null>(null);

function submit() {
  if (!props.busy) {
    emit("confirm");
  }
}

// The focus starts on Cancel, so a stray Enter does not delete anything
onMounted(() => cancelRef.value?.focus());
</script>
