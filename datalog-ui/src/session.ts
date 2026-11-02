/*
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
*/

import { computed, reactive } from "vue";
import type { AuthStatus, AuthUser } from "./types";

// The signed-in user of the UI. Loaded once by the router guard, cleared on a 401 or the sign-out.
export const session = reactive({
  loaded: false,
  setupRequired: false,
  user: null as AuthUser | null
});

export const isAdmin = computed(() => session.user?.role === "admin");

export function applySession(status: AuthStatus) {
  session.loaded = true;
  session.setupRequired = status.setupRequired;
  session.user = status.user;
}

export function clearSession() {
  session.user = null;
}
