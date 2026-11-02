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

import { ref } from "vue";

export type NoticeSeverity = "success" | "info" | "warning" | "error";

export interface Notice {
  id: number;
  severity: NoticeSeverity;
  summary: string;
  detail: string;
}

const notices = ref<Notice[]>([]);
const timers = new Map<number, number>();
let nextId = 1;

export function useNotices() {
  return notices;
}

export function dismissNotice(id: number) {
  const timer = timers.get(id);
  if (timer) {
    window.clearTimeout(timer);
    timers.delete(id);
  }
  notices.value = notices.value.filter((item) => item.id !== id);
}

export function notify(
  severity: NoticeSeverity,
  summary: string,
  detail: string,
  durationMs = 4000
) {
  const id = nextId++;
  notices.value = [...notices.value, { id, severity, summary, detail }];
  if (durationMs > 0) {
    timers.set(
      id,
      window.setTimeout(() => {
        dismissNotice(id);
      }, durationMs)
    );
  }
}
