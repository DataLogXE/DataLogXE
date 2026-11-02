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
  <div class="overlay tag-job-overlay">
    <div class="overlay-card tag-job-dialog" role="dialog" aria-modal="true" :aria-labelledby="titleId">
      <div class="tag-job-header">
        <strong :id="titleId" class="tag-job-line">{{ title }}</strong>
        <div class="tag-job-status" aria-live="polite">
          <template v-for="(item, index) in status" :key="index">
            <p v-if="item.kind === 'hint'" class="hint tag-job-line" :title="item.text">{{ item.text }}</p>
            <span v-else class="badge tag-job-badge" :class="badgeClass[item.kind]">
              <span v-if="item.kind === 'running'" class="spinner spinner-sm" aria-hidden="true"></span>
              <span class="tag-job-line" :title="item.text">{{ item.text }}</span>
            </span>
          </template>
        </div>
      </div>
      <slot />
      <ul ref="logEl" class="tag-job-log">
        <template v-if="!lines.length">
          <li v-for="text in placeholder" :key="text" class="tag-job-log-placeholder">
            <span class="tag-job-line" :title="text">{{ text }}</span>
          </li>
        </template>
        <li
          v-for="(line, index) in lines"
          :key="index"
          :class="{ 'tag-job-log-success': line.success, 'tag-job-log-active': line.active }"
        >
          <span class="tag-job-line" :title="line.text">{{ line.text }}</span>
          <span
            v-if="line.active && line.percent != null"
            class="tag-job-log-progress"
            :style="{ width: `${line.percent}%` }"
            aria-hidden="true"
          ></span>
          <span v-if="line.success" class="tag-job-success-check" aria-hidden="true">
            <svg viewBox="0 0 16 16">
              <rect width="16" height="16" rx="3" />
              <path d="M4 8.2l2.4 2.4L12 5.2" />
            </svg>
          </span>
        </li>
      </ul>
      <div class="actions tag-job-actions">
        <slot name="actions" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
// The common frame of the browse and XML import dialogs of the tag list: one size for all, a status row, an optional
// input row, the log with one text per line (never wrapped, a long one ends in an ellipsis) and the buttons.
import { nextTick, onMounted, ref, watch } from "vue";
import type { TagJobLogLine, TagJobStatus } from "../types";

const props = defineProps<{
  title: string;
  titleId: string;
  status: TagJobStatus[];
  lines: TagJobLogLine[];
  // Shown one text per line while the log is empty
  placeholder: string[];
  // The log follows its last line while the job runs
  follow: boolean;
}>();

const badgeClass: Record<TagJobStatus["kind"], string> = {
  running: "connected",
  warn: "connecting",
  error: "error",
  hint: ""
};

const logEl = ref<HTMLElement | null>(null);

function followLastLine() {
  if (!props.follow) {
    return;
  }
  void nextTick(() => {
    const log = logEl.value;
    if (log) {
      log.scrollTop = log.scrollHeight;
    }
  });
}

// Also when the dialog opens on a running job
onMounted(followLastLine);
watch(() => props.lines, followLastLine);
</script>
