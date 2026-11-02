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
      role="dialog"
      aria-modal="true"
      aria-labelledby="chart-name-title"
      @submit.prevent="submit"
      @keydown.esc.prevent="emit('close')"
    >
      <strong id="chart-name-title">{{ title }}</strong>
      <label class="field">
        <span class="hint">Name</span>
        <input ref="inputRef" v-model="name" type="text" maxlength="100" autocomplete="off" spellcheck="false" />
      </label>
      <p v-if="error" class="error-text">{{ error }}</p>
      <div class="actions chart-dialog-actions">
        <button class="btn secondary" type="button" @click="emit('close')">Cancel</button>
        <button class="btn" type="submit" :disabled="busy || !name.trim()">{{ submitLabel }}</button>
      </div>
    </form>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";

const props = defineProps<{
  title: string;
  initial: string;
  submitLabel: string;
  busy: boolean;
  error: string;
}>();

const emit = defineEmits<{
  submit: [name: string];
  close: [];
}>();

const name = ref(props.initial);
const inputRef = ref<HTMLInputElement | null>(null);

function submit() {
  const value = name.value.trim();
  if (value && !props.busy) {
    emit("submit", value);
  }
}

onMounted(() => {
  inputRef.value?.focus();
  inputRef.value?.select();
});
</script>
