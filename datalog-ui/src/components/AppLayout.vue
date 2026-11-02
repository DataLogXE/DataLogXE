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
  <div class="layout" :class="{ 'is-collapsed': collapsed }">
    <aside class="sidebar">
      <div class="brand">
        <span class="brand-mark" aria-hidden="true">XE</span>
        <span class="brand-text">
          DataLogXE
          <small>{{ appVersion }}</small>
        </span>
      </div>

      <nav class="nav">
        <template v-for="item in items" :key="item.to">
          <div v-if="item.children" class="nav-branch">
            <a
              :href="groupHref(item)"
              class="nav-link"
              :class="{ 'is-active': isItemActive(item) }"
              :data-hint="iconNav ? item.label : undefined"
              :aria-label="item.label"
              @click="onGroupClick(item, $event)"
            >
              <span class="nav-icon" aria-hidden="true">
                <svg v-if="item.icon === 'alarms'" viewBox="0 0 24 24" fill="none">
                  <path d="M6 8.5a6 6 0 0 1 12 0c0 6.2 2.4 7.8 2.4 7.8H3.6S6 14.7 6 8.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
                  <path d="M10 19.2a2 2 0 0 0 4 0" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
                </svg>
              </span>
              <span class="nav-label">{{ item.label }}</span>
            </a>
            <div
              v-if="iconNav || isItemActive(item)"
              class="nav-sub-slot"
              :class="{ 'is-open': isItemActive(item), 'is-rail': iconNav }"
            >
              <div
                class="nav-sub"
                :class="{ 'is-rail': iconNav }"
                role="group"
                :aria-label="item.label"
                :inert="isItemActive(item) ? undefined : true"
              >
                <router-link
                  v-for="child in item.children"
                  :key="child.to"
                  :to="child.to"
                  class="nav-sublink"
                  active-class="is-active"
                  :data-hint="iconNav ? child.label : undefined"
                  :aria-label="child.label"
                >
                  <span class="nav-icon" aria-hidden="true">
                    <svg v-if="child.icon === 'pending'" viewBox="0 0 24 24" fill="none">
                      <path d="M7.5 4.5h9M7.5 19.5h9" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
                      <path d="M8 4.5c0 4.2 4 4.8 4 7.5S8 15.3 8 19.5M16 4.5c0 4.2-4 4.8-4 7.5s4 3.3 4 7.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
                    </svg>
                    <svg v-else-if="child.icon === 'history'" viewBox="0 0 24 24" fill="none">
                      <circle cx="12" cy="12" r="7.25" stroke="currentColor" stroke-width="1.8" />
                      <path d="M12 8.2V12l2.8 1.8" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
                    </svg>
                  </span>
                  <span class="nav-label">{{ child.label }}</span>
                </router-link>
              </div>
            </div>
          </div>
          <router-link
            v-else
            :to="item.to"
            :data-hint="iconNav ? item.label : undefined"
            :aria-label="item.label"
            custom
            v-slot="{ href, navigate }"
          >
            <a
              :href="href"
              class="nav-link"
              :class="{ 'is-active': isItemActive(item) }"
              @click="navigate"
            >
              <span class="nav-icon" aria-hidden="true">
                <svg v-if="item.icon === 'dashboard'" viewBox="0 0 24 24" fill="none">
                  <rect x="3" y="3" width="8" height="8" rx="2" stroke="currentColor" stroke-width="1.8" />
                  <rect x="13" y="3" width="8" height="5" rx="2" stroke="currentColor" stroke-width="1.8" />
                  <rect x="13" y="10" width="8" height="11" rx="2" stroke="currentColor" stroke-width="1.8" />
                  <rect x="3" y="13" width="8" height="8" rx="2" stroke="currentColor" stroke-width="1.8" />
                </svg>
                <svg v-else-if="item.icon === 'sources'" viewBox="0 0 24 24" fill="none">
                  <path d="M9 2.5v4.5M15 2.5v4.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
                  <path d="M6 7h12v5a6 6 0 0 1-12 0V7Z" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" />
                  <path d="M12 18v3.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
                </svg>
                <svg v-else-if="item.icon === 'charts'" viewBox="0 0 24 24" fill="none">
                  <path d="M4 4v15.5h16.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
                  <path d="M7.5 15l3.5-4.5 3 2.5 5-6.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
                </svg>
              </span>
              <span class="nav-label">{{ item.label }}</span>
            </a>
          </router-link>
        </template>
      </nav>

      <div class="nav-bottom">
        <router-link
          v-for="item in footerItems"
          :key="item.to"
          :to="item.to"
          :data-hint="iconNav ? item.label : undefined"
          :aria-label="item.label"
          custom
          v-slot="{ href, navigate }"
        >
          <a
            :href="href"
            class="nav-link"
            :class="{ 'is-active': isItemActive(item) }"
            @click="navigate"
          >
            <span class="nav-icon" aria-hidden="true">
              <svg v-if="item.icon === 'settings'" viewBox="0 0 24 24" fill="none">
                <path
                  d="M10.2 3.4h3.6l.5 2a6.4 6.4 0 0 1 1.6.9l1.9-.8 1.8 3.1-1.4 1.4c.1.5.2 1.1.2 1.6s-.1 1.1-.2 1.6l1.4 1.4-1.8 3.1-1.9-.8a6.4 6.4 0 0 1-1.6.9l-.5 2h-3.6l-.5-2a6.4 6.4 0 0 1-1.6-.9l-1.9.8-1.8-3.1 1.4-1.4a6.6 6.6 0 0 1 0-3.2L4.4 8.6l1.8-3.1 1.9.8a6.4 6.4 0 0 1 1.6-.9l.5-2Z"
                  stroke="currentColor"
                  stroke-width="1.8"
                  stroke-linejoin="round"
                />
                <circle cx="12" cy="12.2" r="2.5" stroke="currentColor" stroke-width="1.8" />
              </svg>
            </span>
            <span class="nav-label">{{ item.label }}</span>
          </a>
        </router-link>


        <button
          class="collapse-btn"
          type="button"
          :data-hint="collapsed ? 'Expand navigation' : 'Collapse navigation'"
          :aria-label="collapsed ? 'Expand navigation' : 'Collapse navigation'"
          :aria-expanded="!collapsed"
          @click="toggleCollapsed"
        >
          <span class="nav-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" fill="none">
              <path
                :d="collapsed ? 'M9 6l6 6-6 6' : 'M15 6l-6 6 6 6'"
                stroke="currentColor"
                stroke-width="1.8"
                stroke-linecap="round"
                stroke-linejoin="round"
              />
            </svg>
          </span>
          <span class="nav-label">{{ collapsed ? "Expand" : "Collapse" }}</span>
        </button>

        <!-- At the very bottom the signed-in user with a menu, and About beside it -->
        <div class="nav-user-bar">
          <button
            v-if="session.user"
            ref="userButton"
            class="nav-user-btn"
            type="button"
            :data-hint="iconNav ? `${session.user.name} · ${session.user.role}` : undefined"
            aria-haspopup="menu"
            :aria-expanded="userMenu !== null"
            :aria-label="`Signed in as ${session.user.name} (${session.user.role})`"
            @click="toggleUserMenu"
          >
            <span class="nav-avatar" aria-hidden="true">{{ session.user.name.slice(0, 1).toUpperCase() }}</span>
            <span class="nav-label nav-user">
              <span class="nav-user-main">{{ session.user.name }}</span>
              <span class="nav-user-name">· {{ session.user.role }}</span>
            </span>
            <svg class="nav-label nav-user-chevron" viewBox="0 0 24 24" fill="none" aria-hidden="true">
              <path d="M7 10l5 5 5-5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
          </button>
          <button
            class="nav-about-btn"
            type="button"
            data-hint="About and license"
            aria-label="About and license"
            aria-haspopup="dialog"
            @click="aboutOpen = true"
          >
            <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
              <circle cx="12" cy="12" r="8.5" stroke="currentColor" stroke-width="1.8" />
              <path d="M12 11v5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
              <circle cx="12" cy="8" r="1.1" fill="currentColor" />
            </svg>
          </button>
        </div>
      </div>
    </aside>
    <Teleport to="body">
      <div
        v-if="userMenu"
        class="tag-context-menu-backdrop"
        @click="userMenu = null"
        @contextmenu.prevent="userMenu = null"
      >
        <div
          ref="userMenuRef"
          class="tag-context-menu nav-user-menu"
          role="menu"
          :style="{ left: `${userMenu.x}px`, bottom: `${userMenu.bottom}px`, width: `${userMenu.width}px` }"
          @click.stop
          @keydown.esc="closeUserMenu"
        >
          <button type="button" role="menuitem" @click="openAccount">
            <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
              <circle cx="12" cy="8.5" r="3.5" stroke="currentColor" stroke-width="1.8" />
              <path d="M5 19.5a7 7 0 0 1 14 0" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
            </svg>
            Account
          </button>
          <button type="button" role="menuitem" @click="signOut">
            <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
              <path d="M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" />
              <path d="M10 8l-4 4 4 4M6 12h10" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
            Sign out
          </button>
        </div>
      </div>
    </Teleport>
    <AboutDialog v-if="aboutOpen" :version="appVersion" @close="aboutOpen = false" />
    <div class="page-column">
      <!-- Above the page, so it pushes the page down instead of covering it, and stays visible over its dialogs -->
      <SystemBusyBanner />
      <main class="page">
        <slot />
      </main>
    </div>
  </div>
</template>

<script setup lang="ts">
import SystemBusyBanner from "./SystemBusyBanner.vue";
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import AboutDialog from "./AboutDialog.vue";
import { logout } from "../api";
import { clearSession, session } from "../session";

const STORAGE_KEY = "navCollapsed";
const COMPACT_NAV_QUERY = "(max-width: 720px)";
const appVersion = `v${__APP_VERSION__}`;

type NavChild = { to: string; label: string; icon: string };
type NavItem = {
  to: string;
  label: string;
  icon: string;
  exact: boolean;
  children?: NavChild[];
};

function readCollapsed(): boolean {
  try {
    return window.localStorage.getItem(STORAGE_KEY) === "1";
  } catch {
    return false;
  }
}

const items: NavItem[] = [
  { to: "/", label: "Dashboard", icon: "dashboard", exact: true },
  { to: "/datasources", label: "Data sources", icon: "sources", exact: false },
  { to: "/charts", label: "Charts", icon: "charts", exact: false },
  {
    to: "/alarms",
    label: "Alarms",
    icon: "alarms",
    exact: false,
    children: [
      { to: "/alarms/pending", label: "Pending", icon: "pending" },
      { to: "/alarms/history", label: "History", icon: "history" }
    ]
  }
];

const footerItems = [
  { to: "/settings", label: "Settings", icon: "settings", exact: true }
];

const route = useRoute();
const router = useRouter();
const collapsed = ref(readCollapsed());
const aboutOpen = ref(false);
const userButton = ref<HTMLButtonElement | null>(null);
const userMenuRef = ref<HTMLDivElement | null>(null);
// The menu of the user opens above the user, at the bottom of the sidebar
const userMenu = ref<{ x: number; bottom: number; width: number } | null>(null);
const compactNav = ref(window.matchMedia(COMPACT_NAV_QUERY).matches);

const iconNav = computed(() => collapsed.value || compactNav.value);

let compactQuery: MediaQueryList | undefined;

function isItemActive(item: { to: string; exact: boolean }): boolean {
  if (item.exact) {
    return route.path === item.to;
  }
  return route.path === item.to || route.path.startsWith(`${item.to}/`);
}

function groupHref(item: NavItem): string {
  if (isItemActive(item)) {
    return route.fullPath;
  }

  return item.children?.[0]?.to ?? item.to;
}

function isPlainClick(event: MouseEvent): boolean {
  return event.button === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
}

function onGroupClick(item: NavItem, event: MouseEvent) {
  if (!isPlainClick(event)) {
    return;
  }

  event.preventDefault();
  if (!isItemActive(item)) {
    void router.push(item.children?.[0]?.to ?? item.to);
  }
}

function onCompactChange(event: MediaQueryListEvent) {
  compactNav.value = event.matches;
}

onMounted(() => {
  compactQuery = window.matchMedia(COMPACT_NAV_QUERY);
  compactNav.value = compactQuery.matches;
  compactQuery.addEventListener("change", onCompactChange);
});

onUnmounted(() => {
  compactQuery?.removeEventListener("change", onCompactChange);
});

watch(
  collapsed,
  (value) => {
    const offset = value ? "var(--nav-rail)" : "var(--nav-width)";
    document.documentElement.style.setProperty("--notice-left", `calc(${offset} + 1rem)`);
  },
  { immediate: true }
);

function toggleUserMenu() {
  const button = userButton.value;
  if (userMenu.value || !button) {
    userMenu.value = null;
    return;
  }
  const rect = button.getBoundingClientRect();
  userMenu.value = {
    x: Math.round(rect.left),
    bottom: Math.round(window.innerHeight - rect.top + 4),
    width: Math.max(168, Math.round(rect.width))
  };
  void nextTick(() => userMenuRef.value?.querySelector<HTMLButtonElement>("button")?.focus());
}

function closeUserMenu() {
  userMenu.value = null;
  userButton.value?.focus();
}

function openAccount() {
  userMenu.value = null;
  void router.push("/settings/account");
}

async function signOut() {
  userMenu.value = null;
  try {
    await logout();
  } catch {
    // The cookie is gone with the next sign-in anyway
  }
  clearSession();
  await router.replace({ name: "login" });
}

function toggleCollapsed() {
  collapsed.value = !collapsed.value;
  window.localStorage.setItem(STORAGE_KEY, collapsed.value ? "1" : "0");
}
</script>
