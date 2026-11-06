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
  <div class="tag-page">
    <div class="page-header">
      <h1>
        Log<template v-if="sourceName">
          · <SourceTypeIcon v-if="sourceType" class="tag-page-type-icon" :type="sourceType" />{{ sourceName }}</template
        >
      </h1>
      <div class="page-header-actions">
        <router-link class="btn secondary" :to="`/datasources/${sourceId}/tags`">Data tags</router-link>
        <router-link class="btn secondary" to="/datasources">Back</router-link>
      </div>
    </div>

    <AppLogView
      id-prefix="source-log"
      :load="load"
      :reload-key="sourceId"
      :display-message="displayMessage"
      :hidden-props="['DsName']"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { useRoute } from "vue-router";
import AppLogView from "../components/AppLogView.vue";
import SourceTypeIcon from "../components/SourceTypeIcon.vue";
import { getDataSourceLog, getDataSources } from "../api";
import type { AppLogParams } from "../types";

const route = useRoute();
const sourceId = computed(() => Number(route.params.id));
const sourceName = ref("");
const sourceType = ref("");

function load(params: AppLogParams, signal: AbortSignal) {
  return getDataSourceLog(sourceId.value, params, signal);
}

// The data source writes its name and id in front of each message; the page shows them in the title
function displayMessage(message: string): string {
  const prefixes = [`Service: "${sourceName.value}" Id: ${sourceId.value} `, `Service: ${sourceName.value} Id: ${sourceId.value} `];
  const prefix = prefixes.find((p) => sourceName.value && message.startsWith(p));
  return prefix ? message.slice(prefix.length) : message;
}

async function loadSource() {
  try {
    const source = (await getDataSources()).find((s) => s.id === sourceId.value);
    sourceName.value = source?.name ?? `#${sourceId.value}`;
    sourceType.value = source?.type ?? "";
  } catch {
    sourceName.value = `#${sourceId.value}`;
  }
}

watch(sourceId, () => void loadSource());
onMounted(() => void loadSource());
</script>
