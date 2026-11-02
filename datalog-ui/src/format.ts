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

const YEAR_SEC = 3600 * 24 * 365;
const MONTH_SEC = 3600 * 24 * 28;
const DAY_SEC = 3600 * 24;
const MINUTE_SEC = 60;
const CURSOR_MS_RANGE_SEC = 5 * MINUTE_SEC;

function displayLocales(): Intl.LocalesArgument {
  if (typeof navigator === "undefined") {
    return undefined;
  }
  if (navigator.languages.length > 0) {
    return Array.from(navigator.languages);
  }
  if (navigator.language) {
    return navigator.language;
  }
  return undefined;
}

const locales = displayLocales();
const numberFormatters = new Map<string, Intl.NumberFormat>();

const dateTimeFormat = new Intl.DateTimeFormat(locales, {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "numeric",
  minute: "2-digit",
  second: "2-digit"
});

const dateTimeMsFormat = new Intl.DateTimeFormat(locales, {
  year: "numeric",
  month: "2-digit",
  day: "2-digit",
  hour: "numeric",
  minute: "2-digit",
  second: "2-digit",
  fractionalSecondDigits: 3
});

const yearFormat = new Intl.DateTimeFormat(locales, { year: "numeric" });
const monthYearFormat = new Intl.DateTimeFormat(locales, { month: "2-digit", year: "numeric" });
const monthDayFormat = new Intl.DateTimeFormat(locales, { month: "2-digit", day: "2-digit" });
const weekdayDayFormat = new Intl.DateTimeFormat(locales, { weekday: "short", month: "2-digit", day: "2-digit" });
const weekdayFormat = new Intl.DateTimeFormat(locales, { weekday: "short" });
const hourOfDayFormat = new Intl.DateTimeFormat(locales, { hour: "2-digit", minute: "2-digit" });
const dateFormat = new Intl.DateTimeFormat(locales, {
  year: "numeric",
  month: "2-digit",
  day: "2-digit"
});
const timeFormat = new Intl.DateTimeFormat(locales, {
  hour: "numeric",
  minute: "2-digit"
});
const timeSecFormat = new Intl.DateTimeFormat(locales, {
  hour: "numeric",
  minute: "2-digit",
  second: "2-digit"
});
const timeMsFormat = new Intl.DateTimeFormat(locales, {
  hour: "numeric",
  minute: "2-digit",
  second: "2-digit",
  fractionalSecondDigits: 3
});

// The live cursor label shows hours with two digits, unlike the axis ticks
const cursorTimeSecFormat = new Intl.DateTimeFormat(locales, {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit"
});
const cursorTimeMsFormat = new Intl.DateTimeFormat(locales, {
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  fractionalSecondDigits: 3
});

function numberFormatter(options?: Intl.NumberFormatOptions): Intl.NumberFormat {
  const key = options ? JSON.stringify(options) : "";
  let formatter = numberFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(locales, options);
    numberFormatters.set(key, formatter);
  }
  return formatter;
}

export function formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
  return numberFormatter(options).format(value);
}

export function formatInteger(value: number): string {
  return numberFormatter({ maximumFractionDigits: 0 }).format(value);
}

export function formatYesNo(value: boolean | null | undefined): string {
  if (value == null) {
    return "—";
  }

  return value ? "Yes" : "No";
}

const UNCONFIRMED_SUFFIX = "Unconfirmed";

export function alarmStateBase(state: string | null | undefined): string {
  if (!state) {
    return "";
  }

  if (state.endsWith(UNCONFIRMED_SUFFIX) && state.length > UNCONFIRMED_SUFFIX.length) {
    return state.slice(0, -UNCONFIRMED_SUFFIX.length);
  }

  return state;
}

const classNameSeparator = "::";

export function formatClassName(name: string | null | undefined): string {
  const text = name?.trim() ?? "";
  const splitAt = text.indexOf(classNameSeparator);
  if (splitAt < 0) {
    return text;
  }

  return text.slice(splitAt + classNameSeparator.length).trim();
}

export function alarmClassLabel(symbol: string | null | undefined, name: string | null | undefined): string {
  const sym = symbol?.trim() ?? "";
  if (sym) {
    return sym;
  }

  return formatClassName(name);
}

export function formatAlarmState(state: string | null | undefined, limitState?: string | null): string {
  const base = alarmStateBase(state);
  let label: string;
  switch (base) {
    case "Incoming":
    case "Incoming/Outgoing":
    case "Removed":
    case "Normal":
      label = base;
      break;
    case "ActiveUnacked":
    case "ActiveAcked":
      label = "Incoming";
      break;
    case "InactiveUnacked":
      label = "Inactive, unacknowledged";
      break;
    case "Inactive":
      label = "Removed";
      break;
    case "Disabled":
      label = "Disabled";
      break;
    case "OutOfService":
      label = "Out of service";
      break;
    case "Suppressed":
      label = "Suppressed";
      break;
    case "Shelved":
      label = "Shelved";
      break;
    case "SuppressedOrShelved":
      label = "Suppressed or shelved";
      break;
    default:
      label = state ?? "";
      break;
  }

  if (label && state && state !== base) {
    label = `${label}, unconfirmed`;
  }

  const limit = limitState?.trim();
  if (label && limit) {
    return `${label} · ${limit}`;
  }

  return label;
}

export function alarmStateMatchesFilter(state: string | null | undefined, filter: string): boolean {
  if (!filter) {
    return true;
  }

  if (!state) {
    return false;
  }

  if (state === filter) {
    return true;
  }

  return alarmStateBase(filter) === filter && state === `${filter}${UNCONFIRMED_SUFFIX}`;
}

export function limitStateFromProps(props: string | null | undefined): string {
  if (!props) {
    return "";
  }

  try {
    const parsed = JSON.parse(props) as { LimitState?: string };
    return parsed.LimitState?.trim() || "";
  } catch {
    return "";
  }
}

export function formatDurationMs(value: number | null | undefined): string {
  if (value == null || value <= 0) {
    return "—";
  }

  if (value < 1000) {
    return `${formatInteger(value)} ms`;
  }

  const totalSeconds = Math.round(value / 1000);
  if (totalSeconds < 60) {
    return `${formatInteger(totalSeconds)} s`;
  }

  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  if (minutes < 60) {
    return seconds === 0
      ? `${formatInteger(minutes)} min`
      : `${formatInteger(minutes)} min ${formatInteger(seconds)} s`;
  }

  const hours = Math.floor(minutes / 60);
  const restMinutes = minutes % 60;
  return restMinutes === 0
    ? `${formatInteger(hours)} h`
    : `${formatInteger(hours)} h ${formatInteger(restMinutes)} min`;
}

function formatSeconds(milliseconds: number): string {
  return numberFormatter({ minimumFractionDigits: 3, maximumFractionDigits: 3, useGrouping: false }).format(milliseconds / 1000);
}

/** A time span for the badge dX: only the units it needs, for example "12,345 s", "3 min 4,500 s" or "1 h 2 min 3,456 s". */
export function formatSpan(seconds: number): string {
  let rest = Math.round(Math.abs(seconds) * 1000);
  const days = Math.floor(rest / 86_400_000);
  rest -= days * 86_400_000;
  const hours = Math.floor(rest / 3_600_000);
  rest -= hours * 3_600_000;
  const minutes = Math.floor(rest / 60_000);
  rest -= minutes * 60_000;
  const parts: string[] = [];
  if (days > 0) {
    parts.push(`${formatInteger(days)} d`);
  }
  if (days > 0 || hours > 0) {
    parts.push(`${hours} h`);
  }
  if (days > 0 || hours > 0 || minutes > 0) {
    parts.push(`${minutes} min`);
  }
  parts.push(`${formatSeconds(rest)} s`);
  return parts.join(" ");
}

/**
 * The same span in up to four resolutions, one per line: in seconds, minutes, hours and days, each with the
 * smaller units. A line whose first number is 0 is left out, the line in seconds always stays.
 */
export function formatSpanLines(seconds: number): string[] {
  const total = Math.round(Math.abs(seconds) * 1000);
  const minutes = Math.floor(total / 60_000);
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);
  const rest = `${formatSeconds(total - minutes * 60_000)} s`;
  const lines = [`${formatSeconds(total)} s`];
  if (minutes > 0) {
    lines.push(`${formatInteger(minutes)} min ${rest}`);
  }
  if (hours > 0) {
    lines.push(`${formatInteger(hours)} h ${minutes - hours * 60} min ${rest}`);
  }
  if (days > 0) {
    lines.push(`${formatInteger(days)} d ${hours - days * 24} h ${minutes - hours * 60} min ${rest}`);
  }
  return lines;
}

export function formatDateTime(date: Date): string {
  return dateTimeFormat.format(date);
}

export function formatDateTimeMs(date: Date): string {
  return dateTimeMsFormat.format(date);
}

/** The day of a bar of days, like "Thu, 10/29". */
export function formatWeekdayDay(date: Date): string {
  return weekdayDayFormat.format(date);
}

/** The two lines of the axis label of a day, like "Thu" and "10/29". */
export function formatWeekdayLines(date: Date): [string, string] {
  return [weekdayFormat.format(date), monthDayFormat.format(date)];
}

/** The start of a bar of hours, like "21:00". */
export function formatHourOfDay(date: Date): string {
  return hourOfDayFormat.format(date);
}

/** The time of the crosshair on the time axis, and its date apart, so the badge can show the time larger. */
export function formatAxisCursorTime(date: Date, rangeSec: number): { time: string; date: string } {
  return { time: cursorTime(date, rangeSec), date: dateFormat.format(date) };
}

function cursorTime(date: Date, rangeSec: number): string {
  return rangeSec < CURSOR_MS_RANGE_SEC - 1e-3
    ? cursorTimeMsFormat.format(date)
    : cursorTimeSecFormat.format(date);
}

export function formatChartCursorTime(date: Date, rangeSec: number): string {
  const time = cursorTime(date, rangeSec);
  if (rangeSec >= DAY_SEC) {
    return `${time}  ${dateFormat.format(date)}`;
  }
  return time;
}

const LABEL_SIGNIFICANT_DIGITS = 6;

function decimalSeparator(): string {
  return new Intl.NumberFormat(locales).formatToParts(1.1).find((part) => part.type === "decimal")?.value ?? ".";
}

/**
 * A value in the labels of the cursors (Y1, Y2, dY and the live cursor label) with 6 significant digits.
 * Zeros after the decimal comma are cut off, but one zero stays where zeros were cut (0,50 and 2,0) and a
 * value without decimals gets one (123457,0). Very big and very small values are written in scientific
 * notation. A bool shows 0 or 1.
 */
export function formatLabelValue(value: number, isBool = false): string {
  if (isBool) {
    return value >= 0.5 ? "1" : "0";
  }
  if (!Number.isFinite(value)) {
    return String(value);
  }
  // + 0 turns -0 into 0
  const rounded = Number(value.toPrecision(LABEL_SIGNIFICANT_DIGITS)) + 0;
  const abs = Math.abs(rounded);
  if (abs >= 1e6 || (abs !== 0 && abs < 1e-6)) {
    return numberFormatter({ notation: "scientific", maximumSignificantDigits: LABEL_SIGNIFICANT_DIGITS }).format(rounded);
  }

  // All 6 digits written out, such as 0.500000, 2.00000 or 100.897
  const written = rounded.toPrecision(LABEL_SIGNIFICANT_DIGITS);
  let text = `${written}.0`;
  if (written.includes(".")) {
    const cut = written.replace(/0+$/, "");
    text = cut.length < written.length ? `${cut}0` : written;
  }
  return text.replace(".", decimalSeparator());
}

export function formatChartValue(value: number, isBool: boolean): string {
  if (isBool) {
    return value >= 0.5 ? "1" : "0";
  }
  if (Math.abs(value) >= 1e6) {
    return numberFormatter({ notation: "scientific", maximumSignificantDigits: 4 }).format(value);
  }
  const rounded = Number(value.toPrecision(5));
  return numberFormatter({ maximumSignificantDigits: 5 }).format(rounded);
}

/**
 * The value at the crosshair on the y axis, with as many decimals as a hundredth of the visible range needs (at most 6),
 * so the badge keeps its width while the mouse moves.
 */
export function formatAxisCursorValue(value: number, min: number, max: number): string {
  if (Math.abs(value) >= 1e6) {
    return numberFormatter({ notation: "scientific", maximumSignificantDigits: 4 }).format(value);
  }
  const span = Math.abs(max - min);
  const decimals = span > 0 ? Math.min(6, Math.max(0, Math.ceil(-Math.log10(span / 100)))) : 2;
  // + 0 turns -0 into 0
  return numberFormatter({ minimumFractionDigits: decimals, maximumFractionDigits: decimals }).format(Number(value.toFixed(decimals)) + 0);
}

export function formatChartTimeTicks(splits: number[], incrSec: number): string[] {
  return splits.map((ts, index) => {
    // Rounded to the millisecond, or a tick at 02,300 s that is stored as 02,29999 would read 02,299
    const date = new Date(Math.round(ts * 1000));
    const prev = index > 0 ? new Date(Math.round(splits[index - 1] * 1000)) : null;

    if (incrSec >= YEAR_SEC) {
      return yearFormat.format(date);
    }
    if (incrSec >= MONTH_SEC) {
      return monthYearFormat.format(date);
    }
    if (incrSec >= DAY_SEC) {
      const label = monthDayFormat.format(date);
      if (!prev || !sameYear(date, prev)) {
        return `${label}\n${yearFormat.format(date)}`;
      }
      return label;
    }

    let time: string;
    if (incrSec >= MINUTE_SEC) {
      time = timeFormat.format(date);
    } else if (incrSec >= 1) {
      time = timeSecFormat.format(date);
    } else {
      time = timeMsFormat.format(date);
    }

    if (!prev || !sameDay(date, prev)) {
      return `${time}\n${dateFormat.format(date)}`;
    }
    return time;
  });
}

/**
 * The first line of a tick of the time axis for the increment, with the digits that take the most room: for the
 * width that the ticks of this increment need.
 */
export function chartTimeTickSample(incrSec: number): string {
  // 20:58:58,888 on 22.12., a day that is not the first or the last of a month, year or week
  const sample = new Date(2026, 11, 22, 20, 58, 58, 888).getTime() / 1000;
  return formatChartTimeTicks([sample], incrSec)[0].split("\n")[0];
}

function sameYear(a: Date, b: Date): boolean {
  return a.getFullYear() === b.getFullYear();
}

function sameDay(a: Date, b: Date): boolean {
  return sameYear(a, b) && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

/** Device clock minus DataLogXE clock (ms) as text: "5 min 43 s ahead of DataLogXE", "6.0 s behind DataLogXE". */
export function formatClockOffset(ms: number): string {
  const abs = Math.abs(ms);
  let span: string;
  if (abs < 60000) {
    span = `${(abs / 1000).toFixed(1)} s`;
  } else {
    const total = Math.round(abs / 1000);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    span = h > 0 ? `${h} h ${m} min` : `${m} min ${total % 60} s`;
  }
  return `${span} ${ms >= 0 ? "ahead of" : "behind"} DataLogXE`;
}
