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
  <section class="form-card settings-card">
    <h2>Account</h2>
    <p class="settings-row">
      <span>Signed in as</span>
      <strong>{{ session.user?.name }} ({{ session.user?.role }})</strong>
    </p>
    <form class="account-form" @submit.prevent="submit">
      <input class="sr-only" :value="session.user?.name ?? ''" autocomplete="username" readonly tabindex="-1" />
      <div class="field">
        <label for="currentPassword">Current password</label>
        <input id="currentPassword" v-model="current" type="password" autocomplete="current-password" required :disabled="busy" />
      </div>
      <div class="field">
        <label for="newPassword">New password</label>
        <input id="newPassword" v-model="next" type="password" autocomplete="new-password" minlength="8" required :disabled="busy" />
      </div>
      <div class="field">
        <label for="repeatPassword">Repeat new password</label>
        <input id="repeatPassword" v-model="repeat" type="password" autocomplete="new-password" required :disabled="busy" />
      </div>
      <p class="hint">At least 8 characters. Your other sessions are signed out.</p>
      <p v-if="error" class="error-text">{{ error }}</p>
      <p v-if="done" class="ok-text">The password was changed.</p>
      <div>
        <button class="btn" type="submit" :disabled="busy">Change password</button>
      </div>
    </form>
  </section>
</template>

<script setup lang="ts">
import { ref } from "vue";
import { changeOwnPassword } from "../api";
import { applySession, session } from "../session";

const current = ref("");
const next = ref("");
const repeat = ref("");
const busy = ref(false);
const error = ref("");
const done = ref(false);

async function submit() {
  error.value = "";
  done.value = false;
  if (next.value !== repeat.value) {
    error.value = "The new passwords do not match.";
    return;
  }
  busy.value = true;
  try {
    applySession(await changeOwnPassword(current.value, next.value));
    current.value = "";
    next.value = "";
    repeat.value = "";
    done.value = true;
  } catch (err) {
    error.value = err instanceof Error ? err.message : "The password could not be changed.";
  } finally {
    busy.value = false;
  }
}
</script>

<style scoped>
.account-form {
  display: grid;
  gap: 0.75rem;
  margin-top: 1rem;
  max-width: 360px;
}

.account-form .hint,
.account-form .error-text,
.ok-text {
  margin: 0;
}

.ok-text {
  color: var(--ok);
}
</style>
