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

export type ThemePreference = "system" | "light" | "dark";
export type Theme = "light" | "dark";

// Per browser, like the other view settings
const STORAGE_KEY = "datalog.theme";
// The plots draw on a canvas and redraw on this event, the rest follows the CSS variables of data-theme
export const THEME_CHANGED_EVENT = "datalog-theme-changed";

const systemDark = window.matchMedia("(prefers-color-scheme: dark)");

export const themePreference = ref<ThemePreference>(loadPreference());
export const theme = ref<Theme>(resolve(themePreference.value));

/** The colors of the canvas of the plots; the cursors match --measure-x1/-x2/-dx of style.css. */
export interface ChartColors {
  line: string;
  boolFill: string;
  staleLine: string;
  staleFill: string;
  axisText: string;
  grid: string;
  xBorder: string;
  yBorder: string;
  now: string;
  cursors: [string, string];
  span: string;
  /**
   * The time grid (timeGrid.ts): bands of every other day and of the weekend, lines of midnight, hours and finer.
   * Midnight in an accent color, so it does not read as a peak of the line: the accent blue in light, gold in dark,
   * where the blue would be close to the cyan line. The hour lines lie above the series and are opaque, so they stay
   * visible across the line.
   */
  dayBand: string;
  weekendBand: string;
  dayLine: string;
  hourLine: string;
  minuteLine: string;
  dayText: string;
}

const LIGHT_CHART: ChartColors = {
  line: "black",
  boolFill: "rgba(0,0,100,0.5)",
  staleLine: "#b8c2cc",
  staleFill: "rgba(184,194,204,0.25)",
  axisText: "#000",
  grid: "rgba(0,0,0,0.07)",
  xBorder: "lightgrey",
  yBorder: "grey",
  now: "#666666",
  cursors: ["#ab4e52", "#436b95"],
  span: "#4a5d23",
  dayBand: "rgba(0,0,0,0.035)",
  weekendBand: "rgba(40,70,170,0.06)",
  dayLine: "rgba(29,111,191,0.6)",
  hourLine: "#cccccc",
  minuteLine: "rgba(0,0,0,0.16)",
  dayText: "rgba(0,0,0,0.55)"
};

// The line in a bright cyan, axis, grid and tick text in muted grays; the cursors and the span muted, since they
// fill labels
const DARK_CHART: ChartColors = {
  line: "#31c0f6",
  boolFill: "rgba(49,192,246,0.3)",
  staleLine: "#333346",
  staleFill: "rgba(51,51,70,0.3)",
  axisText: "#8e91a1",
  grid: "rgba(41,41,51,0.8)",
  xBorder: "#292933",
  yBorder: "#4d4d60",
  now: "#828294",
  cursors: ["#b36a34", "#8f42a7"],
  span: "#077e52",
  dayBand: "rgba(255,255,255,0.035)",
  weekendBand: "rgba(120,140,255,0.07)",
  dayLine: "rgba(214,170,80,0.55)",
  hourLine: "#45464f",
  minuteLine: "rgba(170,172,195,0.22)",
  dayText: "#8e91a1"
};

export function chartColors(): ChartColors {
  return theme.value === "dark" ? DARK_CHART : LIGHT_CHART;
}

function loadPreference(): ThemePreference {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return stored === "light" || stored === "dark" ? stored : "system";
  } catch {
    return "system";
  }
}

function resolve(preference: ThemePreference): Theme {
  if (preference === "system") {
    return systemDark.matches ? "dark" : "light";
  }
  return preference;
}

function apply() {
  const next = resolve(themePreference.value);
  document.documentElement.dataset.theme = next;
  if (theme.value !== next) {
    theme.value = next;
    window.dispatchEvent(new Event(THEME_CHANGED_EVENT));
  }
}

export function setThemePreference(preference: ThemePreference): void {
  themePreference.value = preference;
  try {
    if (preference === "system") {
      window.localStorage.removeItem(STORAGE_KEY);
    } else {
      window.localStorage.setItem(STORAGE_KEY, preference);
    }
  } catch {
    // Without storage the choice holds until the page reloads
  }
  apply();
}

/** Sets data-theme before the app mounts and follows the system while the preference is "system". */
export function installTheme(): void {
  apply();
  systemDark.addEventListener("change", apply);
}
