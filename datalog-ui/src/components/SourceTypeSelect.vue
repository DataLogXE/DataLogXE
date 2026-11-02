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
    :id="id"
    ref="buttonRef"
    class="type-select"
    type="button"
    aria-haspopup="listbox"
    :aria-expanded="open"
    :disabled="disabled"
    :data-hint="selected?.hint"
    @click="toggle"
    @keydown="onButtonKeyDown"
  >
    <SourceTypeIcon :type="modelValue" />
    <span class="type-select-text">{{ selected?.label ?? modelValue }}</span>
    <svg class="type-select-chevron" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
  </button>

  <Teleport to="body">
    <div v-if="open" class="type-select-backdrop" @pointerdown="close" />
    <ul
      v-if="open"
      ref="listRef"
      class="type-select-list"
      role="listbox"
      tabindex="-1"
      :aria-activedescendant="`${id}-option-${active}`"
      :style="listStyle"
      @keydown="onListKeyDown"
    >
      <li
        v-for="(option, index) in options"
        :id="`${id}-option-${index}`"
        :key="option.value"
        class="type-select-option"
        :class="{ 'is-active': index === active, 'is-selected': option.value === modelValue }"
        role="option"
        :aria-selected="option.value === modelValue"
        :data-hint="option.hint"
        data-hint-side="right"
        @pointerenter="active = index"
        @click="choose(index)"
      >
        <SourceTypeIcon :type="option.value" />
        <span>{{ option.label }}</span>
      </li>
    </ul>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref } from "vue";
import SourceTypeIcon from "./SourceTypeIcon.vue";

// A select for the type of a data source that shows the icon of each type; a native select can not show icons
const props = defineProps<{
  id: string;
  modelValue: string;
  options: { value: string; label: string; hint?: string }[];
  disabled?: boolean;
}>();

const emit = defineEmits<{
  "update:modelValue": [value: string];
}>();

const open = ref(false);
const active = ref(0);
const buttonRef = ref<HTMLButtonElement | null>(null);
const listRef = ref<HTMLUListElement | null>(null);
const listStyle = ref<Record<string, string>>({});

const selected = computed(() => props.options.find((option) => option.value === props.modelValue));

function show() {
  const button = buttonRef.value;
  if (!button || props.disabled) {
    return;
  }
  const rect = button.getBoundingClientRect();
  listStyle.value = {
    left: `${Math.round(rect.left)}px`,
    top: `${Math.round(rect.bottom + 2)}px`,
    minWidth: `${Math.round(rect.width)}px`
  };
  active.value = Math.max(0, props.options.findIndex((option) => option.value === props.modelValue));
  open.value = true;
  window.addEventListener("scroll", close, true);
  window.addEventListener("resize", close);
  void nextTick(() => listRef.value?.focus());
}

function close() {
  if (!open.value) {
    return;
  }
  open.value = false;
  window.removeEventListener("scroll", close, true);
  window.removeEventListener("resize", close);
}

function toggle() {
  if (open.value) {
    close();
  } else {
    show();
  }
}

function choose(index: number) {
  const option = props.options[index];
  close();
  buttonRef.value?.focus();
  if (option && option.value !== props.modelValue) {
    emit("update:modelValue", option.value);
  }
}

function onButtonKeyDown(event: KeyboardEvent) {
  if (["ArrowDown", "ArrowUp", "Enter", " "].includes(event.key)) {
    event.preventDefault();
    show();
  }
}

function onListKeyDown(event: KeyboardEvent) {
  const last = props.options.length - 1;
  if (event.key === "ArrowDown") {
    active.value = Math.min(last, active.value + 1);
  } else if (event.key === "ArrowUp") {
    active.value = Math.max(0, active.value - 1);
  } else if (event.key === "Home") {
    active.value = 0;
  } else if (event.key === "End") {
    active.value = last;
  } else if (event.key === "Enter" || event.key === " ") {
    choose(active.value);
  } else if (event.key === "Escape" || event.key === "Tab") {
    close();
    buttonRef.value?.focus();
  } else {
    return;
  }
  event.preventDefault();
  event.stopPropagation();
}

onUnmounted(close);
</script>
