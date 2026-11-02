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
  <div class="notice-host" aria-live="polite" aria-relevant="additions">
    <div
      v-for="item in notices"
      :key="item.id"
      class="notice-card"
      :class="item.severity"
      :role="item.severity === 'error' ? 'alert' : 'status'"
    >
      <span class="notice-icon" aria-hidden="true">
        <svg v-if="item.severity === 'success'" viewBox="0 0 24 24" fill="none">
          <circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="1.8" />
          <path d="M8 12.2l2.6 2.6L16.2 9" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
        </svg>
        <svg v-else-if="item.severity === 'error'" viewBox="0 0 24 24" fill="none">
          <circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="1.8" />
          <path d="M9 9l6 6M15 9l-6 6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
        </svg>
        <svg v-else-if="item.severity === 'warning'" viewBox="0 0 24 24" fill="none">
          <path d="M12 4.8L3.6 19.2h16.8L12 4.8z" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" />
          <path d="M12 10v4.2M12 16.8h.01" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
        </svg>
        <svg v-else viewBox="0 0 24 24" fill="none">
          <circle cx="12" cy="12" r="9" stroke="currentColor" stroke-width="1.8" />
          <path d="M12 10.5V17M12 7.2h.01" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
        </svg>
      </span>
      <div class="notice-body">
        <strong>{{ item.summary }}</strong>
        <p>{{ item.detail }}</p>
      </div>
      <button class="notice-close" type="button" aria-label="Close" @click="dismissNotice(item.id)">
        <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
          <path d="M7 7l10 10M17 7L7 17" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
        </svg>
      </button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { dismissNotice, useNotices } from "../notify";

const notices = useNotices();
</script>
