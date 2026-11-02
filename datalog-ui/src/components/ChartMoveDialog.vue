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
      aria-labelledby="chart-move-title"
      @submit.prevent="submit"
      @keydown.esc.prevent="emit('close')"
    >
      <strong id="chart-move-title">Move "{{ itemName }}"</strong>
      <label class="field">
        <span class="hint">To folder</span>
        <select ref="selectRef" v-model.number="targetId">
          <option v-for="option in options" :key="option.id" :value="option.id" :disabled="!option.allowed">
            {{ "  ".repeat(option.depth) }}{{ option.label }}
          </option>
        </select>
      </label>
      <p v-if="error" class="error-text">{{ error }}</p>
      <div class="actions chart-dialog-actions">
        <button class="btn secondary" type="button" @click="emit('close')">Cancel</button>
        <button class="btn" type="submit" :disabled="busy || targetId == null">Move</button>
      </div>
    </form>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { canMoveTo, folderOptions, type ChartTreeIndex } from "../chart/chartTree";

const props = defineProps<{
  index: ChartTreeIndex;
  itemId: number;
  itemName: string;
  busy: boolean;
  error: string;
}>();

const emit = defineEmits<{
  submit: [parentId: number];
  close: [];
}>();

const selectRef = ref<HTMLSelectElement | null>(null);

const options = computed(() =>
  folderOptions(props.index).map((option) => ({
    ...option,
    allowed: canMoveTo(props.index, props.itemId, option.id)
  }))
);

const targetId = ref<number | null>(options.value.find((option) => option.allowed)?.id ?? null);

function submit() {
  if (targetId.value != null && !props.busy) {
    emit("submit", targetId.value);
  }
}

onMounted(() => selectRef.value?.focus());
</script>
