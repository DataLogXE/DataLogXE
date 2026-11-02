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
    <h2>Users</h2>
    <p class="hint users-text">An admin may change everything, a viewer only reads (dashboard, charts, alarms, tags).</p>
    <p v-if="error" class="error-text">{{ error }}</p>

    <table v-if="users.length" class="users-table">
      <thead>
        <tr>
          <th>Name</th>
          <th>Role</th>
          <th>Last sign-in</th>
          <th><span class="sr-only">Actions</span></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="user in users" :key="user.id">
          <td>{{ user.name }}<span v-if="user.id === session.user?.id" class="hint"> (you)</span></td>
          <td>
            <select :value="user.role" :disabled="busy" :aria-label="`Role of ${user.name}`" @change="changeRole(user, ($event.target as HTMLSelectElement).value)">
              <option value="admin">admin</option>
              <option value="viewer">viewer</option>
            </select>
          </td>
          <td>{{ user.lastLoginAt ? formatDateTime(new Date(user.lastLoginAt)) : "never" }}</td>
          <td class="users-actions">
            <button class="btn secondary" type="button" :disabled="busy" @click="resetPassword(user)">Set password</button>
            <button
              v-if="user.id !== session.user?.id"
              class="btn secondary danger-text"
              type="button"
              :disabled="busy"
              @click="remove(user)"
            >Delete</button>
          </td>
        </tr>
      </tbody>
    </table>

    <form class="users-new" @submit.prevent="add">
      <h3>New user</h3>
      <div class="field">
        <label for="newUserName">Name</label>
        <input id="newUserName" v-model="newName" maxlength="64" autocomplete="off" required :disabled="busy" />
      </div>
      <div class="field">
        <label for="newUserRole">Role</label>
        <select id="newUserRole" v-model="newRole" :disabled="busy">
          <option value="viewer">viewer</option>
          <option value="admin">admin</option>
        </select>
      </div>
      <div class="field">
        <label for="newUserPassword">Password</label>
        <input id="newUserPassword" v-model="newPassword" type="password" autocomplete="new-password" minlength="8" required :disabled="busy" />
      </div>
      <div>
        <button class="btn" type="submit" :disabled="busy">Add user</button>
      </div>
    </form>
  </section>
</template>

<script setup lang="ts">
import { onMounted, ref } from "vue";
import { createUser, deleteUser, getUsers, updateUser } from "../api";
import { formatDateTime } from "../format";
import { session } from "../session";
import type { AppUser, UserRole } from "../types";

const users = ref<AppUser[]>([]);
const busy = ref(false);
const error = ref("");
const newName = ref("");
const newRole = ref<UserRole>("viewer");
const newPassword = ref("");

async function run(work: () => Promise<unknown>) {
  busy.value = true;
  error.value = "";
  try {
    await work();
  } catch (err) {
    error.value = err instanceof Error ? err.message : "The change failed.";
  } finally {
    busy.value = false;
    try {
      users.value = await getUsers();
    } catch {
      // Keep the list shown
    }
  }
}

function add() {
  void run(async () => {
    await createUser({ name: newName.value, role: newRole.value, password: newPassword.value });
    newName.value = "";
    newPassword.value = "";
    newRole.value = "viewer";
  });
}

function changeRole(user: AppUser, role: string) {
  void run(() => updateUser(user.id, { role: role as UserRole }));
}

function resetPassword(user: AppUser) {
  const password = window.prompt(`New password for ${user.name} (at least 8 characters):`);
  if (!password) {
    return;
  }
  void run(() => updateUser(user.id, { password }));
}

function remove(user: AppUser) {
  if (!window.confirm(`Delete the user ${user.name}?`)) {
    return;
  }
  void run(() => deleteUser(user.id));
}

onMounted(() => {
  void run(async () => undefined);
});
</script>

<style scoped>
.users-text {
  margin: 0.6rem 0 0;
}

.users-table {
  width: 100%;
  margin-top: 1rem;
  border-collapse: collapse;
}

.users-table th,
.users-table td {
  padding: 0.35rem 0.5rem 0.35rem 0;
  border-bottom: 1px solid var(--border);
  text-align: left;
  vertical-align: middle;
}

.users-table th {
  color: var(--muted);
  font-weight: 600;
}

.users-actions {
  display: flex;
  gap: 0.4rem;
  justify-content: flex-end;
  flex-wrap: wrap;
}

.danger-text {
  color: var(--danger);
}

.users-new {
  display: grid;
  gap: 0.75rem;
  max-width: 360px;
  margin-top: 1.4rem;
}

.users-new h3 {
  margin: 0;
  font-size: var(--font-section);
}
</style>
