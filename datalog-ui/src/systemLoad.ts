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

import { onUnmounted, ref } from "vue";
import { getRunningQueries, getSystemLoad, type RunningQuery, type SystemLoad } from "./api";
import { session } from "./session";

// Both reads come from the memory of the server, a tab that polls them costs the database nothing
const POLL_MS = 5000;

const load = ref<SystemLoad>({ level: "normal", reason: "", seconds: 0 });
const running = ref<RunningQuery[]>([]);
let users = 0;
let timer: number | undefined;
let polling = false;

async function poll() {
  if (polling || !session.user || document.hidden) {
    return;
  }

  polling = true;
  try {
    const [nextLoad, nextRunning] = await Promise.all([getSystemLoad(), getRunningQueries()]);
    load.value = nextLoad;
    running.value = nextRunning;
  } catch {
    // An unreachable server shows elsewhere; the hint keeps its last state
  } finally {
    polling = false;
  }
}

function onVisible() {
  if (!document.hidden) {
    void poll();
  }
}

/**
 * The level of the load guard and the queries of all clients on the server, polled every 5 s while a component uses
 * them and the tab is visible.
 */
export function useSystemLoad() {
  users += 1;
  if (users === 1) {
    void poll();
    timer = window.setInterval(() => void poll(), POLL_MS);
    document.addEventListener("visibilitychange", onVisible);
  }

  onUnmounted(() => {
    users -= 1;
    if (users === 0) {
      window.clearInterval(timer);
      timer = undefined;
      document.removeEventListener("visibilitychange", onVisible);
    }
  });

  return { load, running, refresh: poll };
}
