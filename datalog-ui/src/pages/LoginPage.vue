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
  <div class="login-page">
    <form class="login-card" @submit.prevent="submit">
      <div class="login-brand">
        <span class="login-logo" aria-hidden="true">XE</span>
        <span>DataLogXE</span>
      </div>
      <h1>{{ setup ? "Create the administrator" : "Sign in" }}</h1>
      <p v-if="setup" class="hint">
        No user exists yet. The administrator manages the other users under Settings.
      </p>

      <div class="field">
        <label for="loginName">Name</label>
        <input
          id="loginName"
          v-model="name"
          autocomplete="username"
          autocapitalize="none"
          spellcheck="false"
          maxlength="64"
          required
          :disabled="busy"
        />
      </div>
      <div class="field">
        <label for="loginPassword">Password</label>
        <input
          id="loginPassword"
          v-model="password"
          type="password"
          :autocomplete="setup ? 'new-password' : 'current-password'"
          maxlength="256"
          required
          :disabled="busy"
        />
      </div>
      <div v-if="setup" class="field">
        <label for="loginRepeat">Repeat password</label>
        <input
          id="loginRepeat"
          v-model="repeat"
          type="password"
          autocomplete="new-password"
          maxlength="256"
          required
          :disabled="busy"
        />
        <span class="hint">At least 8 characters.</span>
      </div>

      <p v-if="error" class="error-text" role="alert">{{ error }}</p>

      <button class="btn login-submit" type="submit" :disabled="busy">
        {{ busy ? "Please wait …" : setup ? "Create and sign in" : "Sign in" }}
      </button>
    </form>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from "vue";
import { useRoute, useRouter } from "vue-router";
import { login, setupAdministrator } from "../api";
import { applySession, session } from "../session";

const route = useRoute();
const router = useRouter();
const name = ref(session.setupRequired ? "admin" : "");
const password = ref("");
const repeat = ref("");
const busy = ref(false);
const error = ref("");
const setup = computed(() => session.setupRequired);

function target(): string {
  const next = route.query.next;
  // Only paths of this app, never another site
  return typeof next === "string" && next.startsWith("/") && !next.startsWith("//") ? next : "/";
}

async function submit() {
  error.value = "";
  if (setup.value && password.value !== repeat.value) {
    error.value = "The passwords do not match.";
    return;
  }
  if (setup.value && password.value.length < 8) {
    error.value = "The password needs at least 8 characters.";
    return;
  }

  busy.value = true;
  try {
    const status = setup.value
      ? await setupAdministrator(name.value, password.value)
      : await login(name.value, password.value);
    applySession(status);
    password.value = "";
    repeat.value = "";
    await router.replace(target());
  } catch (err) {
    error.value = err instanceof Error ? err.message : "The sign-in failed.";
    // Someone else created the administrator meanwhile
    if (setup.value && error.value.includes("already created")) {
      session.setupRequired = false;
    }
  } finally {
    busy.value = false;
  }
}
</script>

<style scoped>
.login-page {
  min-height: 100vh;
  display: grid;
  place-items: center;
  padding: 1rem;
  background: var(--bg);
}

.login-card {
  width: min(380px, 100%);
  display: grid;
  gap: 0.9rem;
  padding: 1.6rem 1.5rem;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.login-card h1 {
  margin: 0;
  font-size: 1.25rem;
}

.login-card .hint {
  margin: 0;
}

.login-brand {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  font-weight: 700;
}

.login-logo {
  display: grid;
  place-items: center;
  width: 2rem;
  height: 2rem;
  border-radius: 8px;
  background: var(--accent);
  color: #fff;
  font-size: 0.85rem;
}

.login-submit {
  width: 100%;
  justify-content: center;
}

.error-text {
  margin: 0;
}
</style>
