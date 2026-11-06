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

import { createRouter, createWebHistory } from "vue-router";
import DashboardPage from "./pages/DashboardPage.vue";
import DataSourceListPage from "./pages/DataSourceListPage.vue";
import DataSourceFormPage from "./pages/DataSourceFormPage.vue";
import DataSourceTagListPage from "./pages/DataSourceTagListPage.vue";
import DataSourceLogPage from "./pages/DataSourceLogPage.vue";
import AlarmsHistoryPage from "./pages/AlarmsHistoryPage.vue";
import AlarmsPendingPage from "./pages/AlarmsPendingPage.vue";
import ChartPage from "./pages/ChartPage.vue";
import SettingsPage from "./pages/SettingsPage.vue";
import LoginPage from "./pages/LoginPage.vue";
import { getAuthStatus } from "./api";
import { applySession, isAdmin, session } from "./session";

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: "/login", name: "login", component: LoginPage, meta: { public: true } },
    { path: "/", name: "dashboard", component: DashboardPage },
    {
      path: "/alarms",
      redirect: (to) => ({
        path: to.query.mode === "archive" ? "/alarms/history" : "/alarms/pending",
        query: to.query
      })
    },
    { path: "/alarms/pending", name: "alarms-pending", component: AlarmsPendingPage },
    { path: "/alarms/history", name: "alarms-history", component: AlarmsHistoryPage },
    { path: "/charts/:id(\\d+)?", name: "charts", component: ChartPage },
    { path: "/settings", name: "settings", component: SettingsPage },
    { path: "/settings/archive", name: "settings-archive", component: SettingsPage },
    { path: "/settings/events", redirect: "/settings/archive" },
    { path: "/settings/account", name: "settings-account", component: SettingsPage },
    { path: "/settings/users", name: "settings-users", component: SettingsPage, meta: { admin: true } },
    { path: "/settings/log", name: "settings-log", component: SettingsPage, meta: { admin: true } },
    { path: "/datasources", name: "datasources", component: DataSourceListPage },
    { path: "/datasources/new", name: "datasource-new", component: DataSourceFormPage, meta: { admin: true } },
    { path: "/datasources/:id/tags", name: "datasource-tags", component: DataSourceTagListPage },
    { path: "/datasources/:id/log", name: "datasource-log", component: DataSourceLogPage },
    { path: "/datasources/:id", name: "datasource-edit", component: DataSourceFormPage, props: true, meta: { admin: true } }
  ]
});

// Every page needs a signed-in user except the sign-in page; a viewer cannot open the edit pages.
router.beforeEach(async (to) => {
  if (!session.loaded) {
    try {
      applySession(await getAuthStatus());
    } catch {
      // The API is not reachable: show the page, its requests report the error.
      session.loaded = true;
    }
  }

  if (to.meta.public) {
    return session.user && to.name === "login" ? (typeof to.query.next === "string" ? to.query.next : "/") : true;
  }
  if (!session.user) {
    return { name: "login", query: to.fullPath === "/" ? {} : { next: to.fullPath } };
  }
  if (to.meta.admin && !isAdmin.value) {
    return to.name === "settings-users" ? { name: "settings" } : { name: "datasources" };
  }
  return true;
});

export default router;
