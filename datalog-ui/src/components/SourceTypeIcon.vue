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
  <svg class="source-type-icon" viewBox="0 0 24 24" aria-hidden="true">
    <!-- System: DataLogXE itself, a server -->
    <template v-if="type === 'System'">
      <rect x="4" y="4" width="16" height="7" rx="1.5" />
      <rect x="4" y="13" width="16" height="7" rx="1.5" />
      <path d="M7.5 7.5h.01M7.5 16.5h.01M11 7.5h5.5M11 16.5h5.5" />
    </template>
    <!-- OPC UA: the tree of nodes of the address space -->
    <template v-else-if="type === 'OpcUa'">
      <rect x="8.5" y="3" width="7" height="6" rx="1.2" />
      <rect x="3" y="15" width="7" height="6" rx="1.2" />
      <rect x="14" y="15" width="7" height="6" rx="1.2" />
      <path d="M12 9v3M6.5 15v-3h11v3" />
    </template>
    <!-- S7CommPlus: a PLC with its modules -->
    <template v-else-if="type === 'S7CommPlus'">
      <rect x="3" y="5" width="18" height="14" rx="1.5" />
      <path d="M8 5v14M13 5v14M15.5 9h3M15.5 12h3M5.5 9h.01" />
    </template>
    <!-- Test: a flask -->
    <template v-else-if="type === 'Test'">
      <path d="M9 3.5h6M10.5 3.5v5.2L5.2 18.2a1.6 1.6 0 0 0 1.4 2.3h10.8a1.6 1.6 0 0 0 1.4-2.3L13.5 8.7V3.5" />
      <path d="M7.4 15h9.2" />
    </template>
    <!-- Any other type: a plug -->
    <template v-else>
      <path d="M9 3v5M15 3v5M6.5 8h11v3a5.5 5.5 0 0 1-11 0zM12 16.5V21" />
    </template>
  </svg>
</template>

<script setup lang="ts">
defineProps<{
  type: string;
}>();
</script>
