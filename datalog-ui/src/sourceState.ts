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

import type { DataSourceListItem } from "./types";

export function typeLabel(type: string): string {
  return type === "OpcUa" ? "OPC UA" : type;
}

// A device clock off by more than the accepted difference of the source (0 = no warning).
export function clockOff(source: DataSourceListItem): boolean {
  return source.clockWarnMs > 0 && source.clockOffsetMs != null && Math.abs(source.clockOffsetMs) > source.clockWarnMs;
}

export function clockSpan(ms: number): string {
  const total = Math.round(Math.abs(ms) / 1000);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h} h ${m} min`;
  if (m > 0) return `${m} min ${s} s`;
  return `${s} s`;
}

export function clockShort(ms: number | null): string {
  return ms == null ? "" : `${ms > 0 ? "+" : "−"}${clockSpan(ms)}`;
}

export function clockText(ms: number | null): string {
  if (ms == null) return "";
  return `The clock of this source is ${clockSpan(ms)} ${ms > 0 ? "ahead of" : "behind"} DataLogXE. `
    + "Please set the clock of the device.";
}

export function badgeClass(state: string): string {
  const key = state.toLowerCase();
  if (key === "connected") return "connected";
  if (key === "error") return "error";
  if (key === "inactive") return "inactive";
  if (key === "loading" || key === "connecting") return "connecting";
  return "default";
}
