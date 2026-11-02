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
    <div class="settings-page">
      <div class="page-header">
        <h1>Settings</h1>
      </div>
      <div class="settings-layout">
        <nav class="settings-nav" aria-label="Settings">
          <router-link
            v-for="item in sections"
            :key="item.to"
            :to="item.to"
            custom
            v-slot="{ href, navigate, isActive, isExactActive }"
          >
            <a
              :href="href"
              class="settings-nav-link"
              :class="{ 'is-active': item.exact ? isExactActive : isActive }"
              :aria-current="(item.exact ? isExactActive : isActive) ? 'page' : undefined"
              @click="navigate"
            >{{ item.label }}</a>
          </router-link>
        </nav>
        <div class="page-body">
          <div v-if="section === 'general'" class="settings-sections">
            <section class="form-card settings-card">
              <h2>Application</h2>
              <p class="settings-row">
                <span>Version</span>
                <strong>{{ appVersion }}</strong>
              </p>
            </section>
            <section class="form-card settings-card">
              <h2 id="settings-theme">Appearance</h2>
              <p class="hint settings-text">Applies to this browser. System follows the light or dark mode of the device.</p>
              <div class="segmented settings-theme" role="radiogroup" aria-labelledby="settings-theme">
                <label v-for="option in themeOptions" :key="option.value" class="segmented-option">
                  <input
                    class="sr-only"
                    type="radio"
                    name="settings-theme"
                    :value="option.value"
                    :checked="themePreference === option.value"
                    @change="setThemePreference(option.value)"
                  />
                  {{ option.label }}
                </label>
              </div>
            </section>
            <section class="form-card settings-card">
              <h2>OPC UA client certificate</h2>
              <p class="hint settings-text">
                Secured OPC UA servers must trust this certificate. Download it and add it to the trusted client
                certificates of the server.
              </p>
              <p v-if="certError" class="error-text">{{ certError }}</p>
              <template v-else-if="clientCert">
                <p class="settings-row">
                  <span>Subject</span>
                  <strong>{{ clientCert.subject }}</strong>
                </p>
                <p class="settings-row">
                  <span>Thumbprint</span>
                  <strong class="settings-mono">{{ clientCert.thumbprint }}</strong>
                </p>
                <p class="settings-row">
                  <span>Valid until</span>
                  <strong>{{ formatDateTime(new Date(clientCert.notAfterUtc)) }}</strong>
                </p>
                <p class="settings-actions">
                  <a class="btn secondary" :href="certDownloadUrl" download>Download certificate</a>
                </p>
              </template>
              <p v-else class="hint">Loading the client certificate…</p>
            </section>
          </div>

          <div v-else-if="section === 'account'" class="settings-sections">
            <AccountCard />
          </div>

          <div v-else-if="section === 'users'" class="settings-sections">
            <UsersCard />
          </div>

          <template v-else>
            <p v-if="error" class="error-text">{{ error }}</p>
            <div v-else-if="archive" class="settings-sections">
              <section class="form-card settings-card">
                <h2>Database</h2>
                <p class="settings-row">
                  <span>Version</span>
                  <strong>{{ textOrDash(archive.databaseVersion) }}</strong>
                </p>
              </section>
              <RetentionCard
                title="Retention"
                hint="Applies to the archived values and the process events."
                :seconds="archive.retentionSeconds"
                :saving="saving || !isAdmin"
                :error="saveError"
                @apply="applyArchiveRetention"
              />
            </div>
            <p v-else-if="busy" class="hint">Loading archive settings…</p>
          </template>
        </div>
      </div>
    </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from "vue";
import { useRoute } from "vue-router";
import {
  OPC_UA_CLIENT_CERTIFICATE_DOWNLOAD,
  getArchiveSettings,
  getOpcUaClientCertificate,
  isAbortError,
  updateArchiveRetention
} from "../api";
import RetentionCard from "../components/RetentionCard.vue";
import AccountCard from "../components/AccountCard.vue";
import UsersCard from "../components/UsersCard.vue";
import { isAdmin } from "../session";
import { formatDateTime } from "../format";
import { setThemePreference, themePreference, type ThemePreference } from "../theme";
import type { ArchiveSettings, OpcUaCertificateInfo } from "../types";

type Section = "general" | "archive" | "account" | "users";

const appVersion = `v${__APP_VERSION__}`;
const themeOptions: { value: ThemePreference; label: string }[] = [
  { value: "system", label: "System" },
  { value: "light", label: "Light" },
  { value: "dark", label: "Dark" }
];
const route = useRoute();
const section = computed<Section>(() => {
  if (route.name === "settings-archive") {
    return "archive";
  }
  if (route.name === "settings-account") {
    return "account";
  }
  if (route.name === "settings-users") {
    return "users";
  }
  return "general";
});
const sections = computed(() => [
  { to: "/settings", label: "General", exact: true },
  { to: "/settings/archive", label: "Archive", exact: false },
  { to: "/settings/account", label: "Account", exact: false },
  ...(isAdmin.value ? [{ to: "/settings/users", label: "Users", exact: false }] : [])
]);

const archive = ref<ArchiveSettings | null>(null);
const busy = ref(false);
const error = ref("");
const saving = ref(false);
const saveError = ref("");
let controller: AbortController | null = null;
const clientCert = ref<OpcUaCertificateInfo | null>(null);
const certError = ref("");
const certDownloadUrl = OPC_UA_CLIENT_CERTIFICATE_DOWNLOAD;

async function loadClientCertificate(signal: AbortSignal) {
  certError.value = "";
  try {
    const result = await getOpcUaClientCertificate(signal);
    clientCert.value = result.certificate;
    if (!result.certificate) {
      certError.value = "The client certificate could not be read.";
    }
  } catch (err) {
    if (!isAbortError(err) && !signal.aborted) {
      certError.value = err instanceof Error ? err.message : "The client certificate could not be read.";
    }
  }
}

function textOrDash(value: string | null | undefined): string {
  return value ? value : "–";
}

async function applyRetention(save: () => Promise<void>) {
  saving.value = true;
  saveError.value = "";
  try {
    await save();
  } catch (err) {
    if (!isAbortError(err)) {
      saveError.value = err instanceof Error ? err.message : "The retention change failed.";
    }
  } finally {
    saving.value = false;
  }
}

function applyArchiveRetention(seconds: number) {
  void applyRetention(async () => {
    archive.value = await updateArchiveRetention(seconds);
  });
}

async function loadArchive() {
  controller?.abort();
  const current = new AbortController();
  controller = current;
  busy.value = true;
  error.value = "";
  saveError.value = "";

  try {
    archive.value = await getArchiveSettings(current.signal);
  } catch (err) {
    if (isAbortError(err) || current.signal.aborted) {
      return;
    }
    archive.value = null;
    error.value = err instanceof Error ? err.message : "The API is not reachable.";
  } finally {
    if (controller === current) {
      busy.value = false;
    }
  }
}

watch(section, (value) => {
  if (value === "account" || value === "users") {
    controller?.abort();
    return;
  }
  if (value !== "general") {
    void loadArchive();
    return;
  }
  controller?.abort();
  const current = new AbortController();
  controller = current;
  void loadClientCertificate(current.signal);
}, { immediate: true });

onUnmounted(() => {
  controller?.abort();
});
</script>

<style scoped>
.settings-text {
  margin: 0.6rem 0 0;
}

.settings-mono {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.8rem;
  overflow-wrap: anywhere;
  text-align: right;
}

.settings-actions {
  margin: 1rem 0 0;
}

.settings-theme {
  margin: 0.85rem 0 0;
}
</style>
