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
import type { QueryCost } from "./types";

/** A database query that runs as a background job on the server. */
export interface QueryActivity {
  id: number;
  label: string;
  startedAt: number;
  phase: string;
  percent: number | null;
  count: number | null;
  /** Known once the server estimated the query. */
  cost: QueryCost | null;
  cancel: () => void;
}

export interface QueryActivityUpdate {
  phase?: string;
  percent?: number | null;
  count?: number | null;
  cost?: QueryCost | null;
}

const activities = ref<QueryActivity[]>([]);
let nextId = 1;
// The server ids of the query jobs of this tab, also of those not shown, so the list of all running queries can tell
// which are its own
const ownJobIds = new Set<string>();

export function addOwnJob(id: string) {
  ownJobIds.add(id);
}

export function removeOwnJob(id: string) {
  ownJobIds.delete(id);
}

export function ownJobs(): string[] {
  return [...ownJobIds];
}

export function useQueryActivities() {
  return activities;
}

/**
 * Registers a running query, so the activity panel can show it once it takes longer than a few
 * seconds. The caller updates the progress and ends the activity when the query settles.
 */
export function beginQueryActivity(label: string, cancel: () => void) {
  const id = nextId++;
  activities.value = [
    ...activities.value,
    { id, label, startedAt: Date.now(), phase: "Starting query", percent: null, count: null, cost: null, cancel }
  ];

  return {
    update(update: QueryActivityUpdate) {
      activities.value = activities.value.map((item) => item.id === id ? { ...item, ...update } : item);
    },
    end() {
      activities.value = activities.value.filter((item) => item.id !== id);
    }
  };
}
