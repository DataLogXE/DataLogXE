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

import type {
  AlarmCounts,
  AlarmClassItem,
  ArchiveSettings,
  ChartCreate,
  ChartDetail,
  ChartPlotsAppendResult,
  ChartTree,
  ChartTreeItem,
  ChartUpdate,
  Dashboard,
  DataSourceDetail,
  DataSourceListItem,
  SourceLogCount,
  SourceLogPage,
  AppLogParams,
  DataSourceWrite,
  DataTagActualValues,
  DataTagDetailsList,
  DataTagListPage,
  DataTagMutationResult,
  EventLogCount,
  EventLogPage,
  EventLogSummary,
  OpcUaBrowseStatus,
  OpcUaConnectionTest,
  AppUser,
  AppUserWrite,
  AuthStatus,
  OpcUaCertificateInfo,
  OpcUaConnectionTestResult,
  S7ConnectionTestResult,
  PlotPoints,
  QueryCost,
  QueryJobStatus,
  QueryProgress
} from "./types";
import { addOwnJob, beginQueryActivity, ownJobs, removeOwnJob } from "./queryActivity";
import { clearSession } from "./session";

export class ApiError extends Error {
  readonly status: number;
  readonly aborted: boolean;
  readonly timeout: boolean;
  /** The user stopped the query in the activity panel. The page shows that instead of loading on. */
  readonly userCancelled: boolean;

  constructor(message: string, status: number, aborted = false, timeout = false, userCancelled = false) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.aborted = aborted;
    this.timeout = timeout;
    this.userCancelled = userCancelled;
  }
}

export function isAbortError(error: unknown): boolean {
  return error instanceof ApiError
    ? error.aborted
    : error instanceof DOMException && error.name === "AbortError";
}

/**
 * True when the caller aborted the request or the API reported that a newer request superseded it.
 * A client timeout is a real failure and must not be treated as a cancellation.
 */
export function isCancelledError(error: unknown): boolean {
  if (error instanceof ApiError) {
    return !error.userCancelled && (error.status === 499 || (error.aborted && !error.timeout));
  }

  return error instanceof DOMException && error.name === "AbortError";
}

export function isTimeoutError(error: unknown): boolean {
  return error instanceof ApiError && error.timeout;
}

export function createPollSession() {
  let generation = 0;
  let controller: AbortController | null = null;

  return {
    begin() {
      controller?.abort();
      controller = new AbortController();
      const gen = ++generation;
      const signal = controller.signal;
      return {
        gen,
        signal,
        isCurrent: () => gen === generation
      };
    },
    abort() {
      generation += 1;
      controller?.abort();
      controller = null;
    }
  };
}

// Plain requests answer from memory or write a few rows. A busy Raspberry Pi can still need a while,
// so the limits stay well above the server deadlines; database reads run as jobs without a limit.
const READ_TIMEOUT_MS = 35000;
const WRITE_TIMEOUT_MS = 125000;

function timeoutSignal(timeoutMs: number, parent?: AbortSignal): { signal: AbortSignal; cancel: () => void } {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort("timeout"), timeoutMs);

  const onParentAbort = () => controller.abort(parent?.reason);
  parent?.addEventListener("abort", onParentAbort, { once: true });

  return {
    signal: controller.signal,
    cancel: () => {
      window.clearTimeout(timer);
      parent?.removeEventListener("abort", onParentAbort);
    }
  };
}

// A request without a valid session: forget the user and go to the sign-in page, once.
let redirectingToLogin = false;
function onUnauthorized() {
  clearSession();
  if (redirectingToLogin || window.location.pathname === "/login") {
    return;
  }
  redirectingToLogin = true;
  const next = window.location.pathname + window.location.search;
  window.location.assign(`/login?next=${encodeURIComponent(next)}`);
}

async function apiRequest<T>(
  path: string,
  options: {
    method?: string;
    body?: unknown;
    signal?: AbortSignal;
    timeoutMs?: number;
    headers?: Record<string, string>;
  } = {}
): Promise<T> {
  const timeoutMs = options.timeoutMs ?? READ_TIMEOUT_MS;
  const timed = timeoutSignal(timeoutMs, options.signal);

  try {
    const response = await fetch(path, {
      method: options.method ?? "GET",
      cache: "no-store",
      headers: options.body === undefined
        ? options.headers
        : { ...options.headers, "Content-Type": "application/json" },
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: timed.signal
    });

    if (response.status === 204) {
      return undefined as T;
    }

    const text = await response.text();
    const data = text ? JSON.parse(text) as unknown : null;

    if (!response.ok) {
      if (response.status === 401 && !path.startsWith("/api/auth/")) {
        onUnauthorized();
      }
      const message = (data as { error?: string } | null)?.error
        ?? `Request failed (${response.status})`;
      throw new ApiError(message, response.status, false, response.status === 408);
    }

    return data as T;
  } catch (error) {
    if (error instanceof ApiError) {
      throw error;
    }

    const aborted = isAbortError(error) || timed.signal.aborted;
    const timeout = aborted && (timed.signal.reason === "timeout" || options.signal?.aborted !== true);
    if (aborted) {
      throw new ApiError(
        timeout ? "The request timed out." : "The request was cancelled.",
        timeout ? 408 : 499,
        true,
        timeout
      );
    }

    throw new ApiError("The API is not reachable.", 0);
  } finally {
    timed.cancel();
  }
}

const JOB_POLL_WAIT_MS = 1500;
const JOB_REQUEST_TIMEOUT_MS = 20000;
// A busy machine may not answer a poll for a while. The job keeps running meanwhile, so the client
// keeps asking for this long before it reports the API as unreachable.
const JOB_UNREACHABLE_AFTER_MS = 90000;
const JOB_ID_HEADER = "X-Query-Job-Id";

/**
 * A random id for a new job. The client names the job itself, so it can cancel it even when it gives up before the
 * answer to the start arrives. crypto.randomUUID needs a secure context, the UI is also served over plain http.
 */
function newJobId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
}

export interface QueryJobOptions {
  signal?: AbortSignal;
  /** Shown in the activity panel when the query takes longer than a few seconds. */
  label: string;
  /** A helper query the user does not wait for, for example a filter list. Not shown in the panel. */
  background?: boolean;
  onProgress?: (progress: QueryProgress) => void;
}

function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = window.setTimeout(done, ms);
    signal.addEventListener("abort", done, { once: true });
    function done() {
      window.clearTimeout(timer);
      signal.removeEventListener("abort", done);
      resolve();
    }
  });
}

function isTransientError(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 0 || error.timeout || error.status >= 502);
}

/**
 * Runs a database read as a background job on the server: start it, then long-poll its state until
 * it is done. No single request stays open for long, so a slow machine never produces a timeout.
 * Aborting the signal (or the Cancel button of the activity panel) cancels the job on the server.
 */
async function runQueryJob<T>(path: string, query: string, options: QueryJobOptions): Promise<T> {
  const controller = new AbortController();
  let userCancelled = false;
  const onParentAbort = () => controller.abort(options.signal?.reason);
  if (options.signal?.aborted) {
    throw new ApiError("The request was cancelled.", 499, true, false);
  }
  options.signal?.addEventListener("abort", onParentAbort, { once: true });

  const activity = options.background
    ? { update: () => undefined, end: () => undefined }
    : beginQueryActivity(options.label, () => {
      userCancelled = true;
      controller.abort("user");
    });
  const signal = controller.signal;
  // Known before the start is answered, so an abort at any time cancels the job on the server.
  let jobId: string | null = newJobId();
  const ownId = jobId;
  addOwnJob(ownId);

  const cancelled = () => userCancelled
    ? new ApiError("The query was cancelled.", 499, false, false, true)
    : new ApiError("The request was cancelled.", 499, true, false);

  async function request(method: string, url: string, headers?: Record<string, string>): Promise<QueryJobStatus<T>> {
    let failingSince = 0;
    let attempt = 0;
    for (;;) {
      try {
        return await apiRequest<QueryJobStatus<T>>(url, { method, signal, timeoutMs: JOB_REQUEST_TIMEOUT_MS, headers });
      } catch (error) {
        if (signal.aborted) {
          throw cancelled();
        }

        if (!isTransientError(error)) {
          throw error;
        }

        failingSince ||= Date.now();
        if (Date.now() - failingSince > JOB_UNREACHABLE_AFTER_MS) {
          throw new ApiError("The API is not reachable.", 0);
        }

        activity.update({ phase: "Waiting for the server" });
        attempt += 1;
        await delay(Math.min(5000, 500 * attempt), signal);
        if (signal.aborted) {
          throw cancelled();
        }
      }
    }
  }

  try {
    // A retried start with the same id gets the job that already runs.
    let status = await request("POST", `/api/jobs/${path}${query}`, { [JOB_ID_HEADER]: jobId });
    jobId = status.id;
    for (;;) {
      const progress: QueryProgress = {
        state: status.state,
        phase: status.phase ?? "",
        percent: status.percent ?? null,
        count: status.count ?? null,
        elapsedMs: status.elapsedMs
      };
      activity.update({ phase: progress.phase, percent: progress.percent, count: progress.count, cost: status.cost });
      options.onProgress?.(progress);

      if (status.state === "done") {
        jobId = null;
        return status.result as T;
      }

      if (status.state === "failed") {
        jobId = null;
        throw new ApiError(status.error ?? "The query failed.", status.errorStatus ?? 500);
      }

      if (status.state === "cancelled") {
        jobId = null;
        throw cancelled();
      }

      status = await request("GET", `/api/jobs/${encodeURIComponent(status.id)}?waitMs=${JOB_POLL_WAIT_MS}`);
    }
  } finally {
    options.signal?.removeEventListener("abort", onParentAbort);
    activity.end();
    removeOwnJob(ownId);
    if (jobId && signal.aborted) {
      // Frees the database right away instead of waiting for the server to notice the lost client.
      void apiRequest<void>(`/api/jobs/${encodeURIComponent(jobId)}`, { method: "DELETE", timeoutMs: JOB_REQUEST_TIMEOUT_MS })
        .catch(() => undefined);
    }
  }
}

/** The level of the load guard of the server: tense or critical while the device is busy. */
export interface SystemLoad {
  level: "normal" | "tense" | "critical";
  reason: string;
  seconds: number;
}

export function getSystemLoad(signal?: AbortSignal): Promise<SystemLoad> {
  return apiRequest<SystemLoad>("/api/system/load", { signal, timeoutMs: READ_TIMEOUT_MS });
}

/** A query job of any client on the server. */
export interface RunningQuery {
  key: string;
  kind: string;
  cost: QueryCost;
  state: string;
  phase: string;
  percent: number | null;
  elapsedMs: number;
  runningMs: number;
  clients: number;
  mine: boolean;
}

export function getRunningQueries(signal?: AbortSignal): Promise<RunningQuery[]> {
  const query = new URLSearchParams({ mine: ownJobs().join(",") });
  return apiRequest<RunningQuery[]>(`/api/jobs/running?${query}`, { signal, timeoutMs: READ_TIMEOUT_MS });
}

/** Stops a query of any client (administrators only); its clients get the reason. */
export function stopRunningQuery(key: string): Promise<void> {
  return apiRequest<void>(`/api/jobs/running/${encodeURIComponent(key)}/stop`, { method: "POST", timeoutMs: WRITE_TIMEOUT_MS });
}

export function getArchiveSettings(signal?: AbortSignal): Promise<ArchiveSettings> {
  return apiRequest<ArchiveSettings>("/api/settings/archive", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function updateArchiveRetention(everySeconds: number, signal?: AbortSignal): Promise<ArchiveSettings> {
  return apiRequest<ArchiveSettings>("/api/settings/archive/retention", {
    method: "PUT",
    body: { everySeconds },
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function getDashboard(signal?: AbortSignal): Promise<Dashboard> {
  return apiRequest<Dashboard>("/api/dashboard", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function getDataSources(signal?: AbortSignal): Promise<DataSourceListItem[]> {
  return apiRequest<DataSourceListItem[]>("/api/datasources", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function getDataSourceLog(
  id: number,
  params: AppLogParams,
  signal?: AbortSignal
): Promise<SourceLogPage> {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== "") {
      query.set(key, String(value));
    }
  }
  const suffix = query.size ? `?${query}` : "";
  return apiRequest<SourceLogPage>(`/api/datasources/${id}/log${suffix}`, { signal, timeoutMs: READ_TIMEOUT_MS });
}

function logQuery(params: Record<string, string | number | boolean | undefined>): URLSearchParams {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== "") {
      query.set(key, String(value));
    }
  }
  return query;
}

/** The log of the whole system (administrator only), newest first, paged like the log of a data source. */
export function getSystemLog(params: AppLogParams, signal?: AbortSignal): Promise<SourceLogPage> {
  const query = logQuery({ ...params });
  const suffix = query.size ? `?${query}` : "";
  return apiRequest<SourceLogPage>(`/api/system/log${suffix}`, { signal, timeoutMs: READ_TIMEOUT_MS });
}

/** Filters, range (ISO 8601) and format of a download of the system log. */
export interface SystemLogExportParams {
  from: string;
  to: string;
  format: "csv" | "ndjson";
  decimalComma: boolean;
  level?: string;
  q?: string;
  component?: string;
  source?: string | number;
  correlation?: string;
}

/**
 * The URL of the system log as a download, which the server streams while it reads; checkSystemLogExport asks first
 * whether the server accepts it, since a download cannot show an error.
 */
export function systemLogExportUrl(params: SystemLogExportParams): string {
  return `/api/system/log/export?${logQuery({ ...params })}`;
}

export function checkSystemLogExport(params: SystemLogExportParams, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/system/log/export?${logQuery({ ...params, check: true })}`, { signal });
}

/** The support bundle: the log, the log files and the state of the system as one ZIP file. */
export function diagnosticsUrl(hours: number): string {
  return `/api/system/diagnostics.zip?${logQuery({ hours })}`;
}

export function checkDiagnostics(hours: number, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/system/diagnostics.zip?${logQuery({ hours, check: true })}`, { signal });
}

export function getDataSourceLogCounts(signal?: AbortSignal): Promise<SourceLogCount[]> {
  return apiRequest<SourceLogCount[]>("/api/datasources/log-counts", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function getDataSource(id: number, signal?: AbortSignal): Promise<DataSourceDetail> {
  return apiRequest<DataSourceDetail>(`/api/datasources/${id}`, { signal, timeoutMs: WRITE_TIMEOUT_MS });
}

export function createDataSource(body: DataSourceWrite, signal?: AbortSignal): Promise<DataSourceDetail> {
  return apiRequest<DataSourceDetail>("/api/datasources", {
    method: "POST",
    body,
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function updateDataSource(id: number, body: DataSourceWrite, signal?: AbortSignal): Promise<DataSourceDetail> {
  return apiRequest<DataSourceDetail>(`/api/datasources/${id}`, {
    method: "PUT",
    body,
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function testOpcUaConnection(
  body: OpcUaConnectionTest,
  signal?: AbortSignal
): Promise<OpcUaConnectionTestResult> {
  return apiRequest<OpcUaConnectionTestResult>("/api/datasources/opcua/connection-test", {
    method: "POST",
    body,
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

export function testS7CommPlusConnection(
  body: OpcUaConnectionTest,
  signal?: AbortSignal
): Promise<S7ConnectionTestResult> {
  return apiRequest<S7ConnectionTestResult>("/api/datasources/s7commplus/connection-test", {
    method: "POST",
    body,
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

export function getOpcUaClientCertificate(signal?: AbortSignal): Promise<{ certificate: OpcUaCertificateInfo | null }> {
  return apiRequest<{ certificate: OpcUaCertificateInfo | null }>("/api/opcua/client-certificate", {
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

export const OPC_UA_CLIENT_CERTIFICATE_DOWNLOAD = "/api/opcua/client-certificate/download";

export function getAuthStatus(signal?: AbortSignal): Promise<AuthStatus> {
  return apiRequest<AuthStatus>("/api/auth/status", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function login(name: string, password: string): Promise<AuthStatus> {
  return apiRequest<AuthStatus>("/api/auth/login", { method: "POST", body: { name, password }, timeoutMs: WRITE_TIMEOUT_MS });
}

export function setupAdministrator(name: string, password: string): Promise<AuthStatus> {
  return apiRequest<AuthStatus>("/api/auth/setup", { method: "POST", body: { name, password }, timeoutMs: WRITE_TIMEOUT_MS });
}

export function logout(): Promise<void> {
  return apiRequest<void>("/api/auth/logout", { method: "POST", timeoutMs: WRITE_TIMEOUT_MS });
}

export function changeOwnPassword(current: string, next: string): Promise<AuthStatus> {
  return apiRequest<AuthStatus>("/api/auth/password", {
    method: "POST",
    body: { current, new: next },
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function getUsers(signal?: AbortSignal): Promise<AppUser[]> {
  return apiRequest<AppUser[]>("/api/users", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function createUser(body: AppUserWrite): Promise<AppUser> {
  return apiRequest<AppUser>("/api/users", { method: "POST", body, timeoutMs: WRITE_TIMEOUT_MS });
}

export function updateUser(id: number, body: AppUserWrite): Promise<AppUser> {
  return apiRequest<AppUser>(`/api/users/${id}`, { method: "PUT", body, timeoutMs: WRITE_TIMEOUT_MS });
}

export function deleteUser(id: number): Promise<void> {
  return apiRequest<void>(`/api/users/${id}`, { method: "DELETE", timeoutMs: WRITE_TIMEOUT_MS });
}

export function deleteDataSource(id: number, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/datasources/${id}`, {
    method: "DELETE",
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function getDataTags(
  dataSourceId: number,
  params: {
    q?: string;
    activeOnly?: boolean;
    offset?: number;
    limit?: number;
    sort1?: string;
    dir1?: string;
    sort2?: string;
    dir2?: string;
  },
  signal?: AbortSignal
): Promise<DataTagListPage> {
  const query = new URLSearchParams();
  if (params.q) {
    query.set("q", params.q);
  }
  query.set("activeOnly", params.activeOnly === false ? "false" : "true");
  query.set("offset", String(params.offset ?? 0));
  query.set("limit", String(params.limit ?? 150));
  if (params.sort1 && params.dir1) {
    query.set("sort1", params.sort1);
    query.set("dir1", params.dir1);
  }
  if (params.sort2 && params.dir2) {
    query.set("sort2", params.sort2);
    query.set("dir2", params.dir2);
  }
  return apiRequest<DataTagListPage>(`/api/datasources/${dataSourceId}/tags?${query}`, {
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

export function getDataTagActualValues(
  dataSourceId: number,
  ids: number[],
  signal?: AbortSignal
): Promise<DataTagActualValues> {
  return apiRequest<DataTagActualValues>(`/api/datasources/${dataSourceId}/tags/actual-values`, {
    method: "POST",
    body: { ids },
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

export function setDataTagsActive(
  dataSourceId: number,
  ids: number[],
  isActive: boolean,
  signal?: AbortSignal
): Promise<DataTagMutationResult> {
  return apiRequest<DataTagMutationResult>(`/api/datasources/${dataSourceId}/tags/active`, {
    method: "POST",
    body: { ids, isActive },
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function getDataTagDetails(
  dataSourceId: number,
  ids: number[],
  signal?: AbortSignal
): Promise<DataTagDetailsList> {
  return apiRequest<DataTagDetailsList>(`/api/datasources/${dataSourceId}/tags/details`, {
    method: "POST",
    body: { ids },
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

/** Sets active and cycle of the tags at once; a value left out stays as it is. */
export function updateDataTags(
  dataSourceId: number,
  ids: number[],
  changes: { isActive?: boolean; cycle?: number },
  signal?: AbortSignal
): Promise<DataTagMutationResult> {
  return apiRequest<DataTagMutationResult>(`/api/datasources/${dataSourceId}/tags/update`, {
    method: "POST",
    body: { ids, ...changes },
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function setDataTagsCycle(
  dataSourceId: number,
  ids: number[],
  cycle: number,
  signal?: AbortSignal
): Promise<DataTagMutationResult> {
  return apiRequest<DataTagMutationResult>(`/api/datasources/${dataSourceId}/tags/cycle`, {
    method: "POST",
    body: { ids, cycle },
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function getPlotPoints(
  dataSourceId: number,
  tagId: number,
  from: number,
  to: number,
  width: number,
  signal?: AbortSignal,
  onProgress?: (progress: QueryProgress) => void,
  options: { label?: string; background?: boolean } = {}
): Promise<PlotPoints> {
  const query = new URLSearchParams();
  query.set("from", String(from));
  query.set("to", String(to));
  query.set("width", String(Math.round(width)));
  return runQueryJob<PlotPoints>(`datasources/${dataSourceId}/tags/${tagId}/plot-points`, `?${query}`, {
    signal,
    label: options.label ?? "Loading chart data",
    background: options.background,
    onProgress
  });
}

/**
 * A coarse preview of the points of a range, read quickly from a compression level with about one point per 2
 * pixels; the plot draws it grayed until the exact points of getPlotPoints arrive. Not shown in the activity panel.
 */
export function getPlotPreview(
  dataSourceId: number,
  tagId: number,
  from: number,
  to: number,
  width: number,
  signal?: AbortSignal
): Promise<PlotPoints> {
  const query = new URLSearchParams();
  query.set("from", String(from));
  query.set("to", String(to));
  query.set("width", String(Math.round(width)));
  return runQueryJob<PlotPoints>(`datasources/${dataSourceId}/tags/${tagId}/plot-preview`, `?${query}`, {
    signal,
    label: "Loading chart preview",
    background: true
  });
}

/** The raw value of a data tag at a time (unix seconds), read from the archive as it is stored. */
export interface RawValueAt {
  dataTagId: number;
  /** When the value was recorded; null when the archive has none before the time. */
  time: number | null;
  /** Null when there is no value, or it is NaN or invalid. */
  value: number | null;
}

/**
 * Runs as a query job, so the server reads only a few cursors at a time; aborting the signal (the cursor moved on, the
 * chart closed) cancels the read in the database. Not shown in the activity panel.
 */
export function getRawValueAt(dataSourceId: number, tagId: number, time: number, signal?: AbortSignal): Promise<RawValueAt> {
  const query = new URLSearchParams({ time: String(time) });
  return runQueryJob<RawValueAt>(`datasources/${dataSourceId}/tags/${tagId}/value-at`, `?${query}`, {
    signal,
    label: "Reading the cursor value",
    background: true
  });
}

/** The statistics of the raw values of a data tag between two times, calculated by the archive. */
export interface TagStatistics {
  dataTagId: number;
  name: string;
  isBool: boolean;
  /** The range in unix seconds. */
  from: number;
  to: number;
  /** The valid raw values in the range; NaN and invalid values are not counted. */
  count: number;
  min: number | null;
  max: number | null;
  mean: number | null;
  /** Sample standard deviation, null with fewer than two values. */
  stdDev: number | null;
  /** For a bool tag how long it was high, low and invalid in the range in seconds, else null. */
  highSeconds: number | null;
  lowSeconds: number | null;
  /** For a bool tag how often it changed between 0 and 1 in the range, else null. */
  signalChanges: number | null;
  /** The longest and shortest high and low time of a bool tag; the shortest only of the times that began and ended with a change. */
  longestHighSeconds: number | null;
  shortestHighSeconds: number | null;
  longestLowSeconds: number | null;
  shortestLowSeconds: number | null;
  invalidSeconds: number | null;
}

export function getTagStatistics(
  dataSourceId: number,
  tagId: number,
  from: number,
  to: number,
  signal?: AbortSignal
): Promise<TagStatistics> {
  const query = new URLSearchParams({ from: String(from), to: String(to) });
  return runQueryJob<TagStatistics>(`datasources/${dataSourceId}/tags/${tagId}/statistics`, `?${query}`, {
    signal,
    label: "Calculating statistics"
  });
}

/** The raw values of a data tag for an export, at most limit rows; truncated when the range holds more. */
export interface RawValues {
  dataTagId: number;
  name: string;
  isBool: boolean;
  truncated: boolean;
  times: number[];
  raw: (number | null)[];
}

export function getRawValues(
  dataSourceId: number,
  tagId: number,
  from: number,
  to: number,
  limit: number,
  signal?: AbortSignal,
  onProgress?: (progress: QueryProgress) => void
): Promise<RawValues> {
  const query = new URLSearchParams({ from: String(from), to: String(to), limit: String(Math.round(limit)) });
  return runQueryJob<RawValues>(`datasources/${dataSourceId}/tags/${tagId}/raw-values`, `?${query}`, {
    signal,
    label: "Loading raw values",
    onProgress
  });
}

/** One data tag of the raw data CSV export. */
export interface RawCsvTag {
  dataSourceId: number;
  tagId: number;
}

function rawCsvQuery(tags: RawCsvTag[], from: number, to: number, decimalComma: boolean, offsets: string[], file: string): URLSearchParams {
  const query = new URLSearchParams({ from: String(from), to: String(to), decimalComma: String(decimalComma), file });
  for (const tag of tags) {
    query.append("tag", `${tag.dataSourceId}:${tag.tagId}`);
  }
  for (const offset of offsets) {
    query.append("offset", offset);
  }
  return query;
}

/**
 * The URL of the raw data of the tags as CSV, which the server streams while it reads the archive, so the file has no
 * row limit; checkRawCsv asks first whether the server accepts it, since a download cannot show an error.
 */
export function rawCsvUrl(tags: RawCsvTag[], from: number, to: number, decimalComma: boolean, offsets: string[], file: string): string {
  return `/api/export/raw.csv?${rawCsvQuery(tags, from, to, decimalComma, offsets, file)}`;
}

export function checkRawCsv(
  tags: RawCsvTag[],
  from: number,
  to: number,
  decimalComma: boolean,
  offsets: string[],
  file: string,
  signal?: AbortSignal
): Promise<void> {
  const query = rawCsvQuery(tags, from, to, decimalComma, offsets, file);
  query.set("check", "true");
  return apiRequest<void>(`/api/export/raw.csv?${query}`, { signal });
}

/** The number of raw values of a data tag in the range, from the counters of the archive; null when unknown. */
export async function getRawValueCount(
  dataSourceId: number,
  tagId: number,
  from: number,
  to: number,
  signal?: AbortSignal
): Promise<number | null> {
  const query = new URLSearchParams({ from: String(from), to: String(to) });
  const result = await runQueryJob<{ dataTagId: number; count: number | null }>(
    `datasources/${dataSourceId}/tags/${tagId}/raw-count`,
    `?${query}`,
    { signal, label: "Counting raw values", background: true }
  );
  return result.count;
}

export function getCharts(signal?: AbortSignal): Promise<ChartTree> {
  return apiRequest<ChartTree>("/api/charts", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function getChart(id: number, signal?: AbortSignal): Promise<ChartDetail> {
  return apiRequest<ChartDetail>(`/api/charts/${id}`, { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function createChart(body: ChartCreate, signal?: AbortSignal): Promise<ChartTreeItem> {
  return apiRequest<ChartTreeItem>("/api/charts", {
    method: "POST",
    body,
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function updateChart(id: number, body: ChartUpdate, signal?: AbortSignal): Promise<ChartTreeItem> {
  return apiRequest<ChartTreeItem>(`/api/charts/${id}`, {
    method: "PUT",
    body,
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function deleteChart(id: number, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/charts/${id}`, {
    method: "DELETE",
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

/** Puts a chart or folder below a folder, before its child `beforeId` or last. */
export function moveChart(id: number, parentId: number, beforeId: number | null, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/charts/${id}/move`, {
    method: "POST",
    body: { parentId, beforeId },
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

/** Sorts the charts and folders directly below a folder by name. */
export function sortChartChildren(id: number, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/charts/${id}/sort`, {
    method: "POST",
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function appendChartPlots(id: number, dataTagIds: number[], signal?: AbortSignal): Promise<ChartPlotsAppendResult> {
  return apiRequest<ChartPlotsAppendResult>(`/api/charts/${id}/plots`, {
    method: "POST",
    body: { dataTagIds },
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function startOpcUaBrowse(dataSourceId: number, signal?: AbortSignal): Promise<OpcUaBrowseStatus> {
  return apiRequest<OpcUaBrowseStatus>(`/api/datasources/${dataSourceId}/tags/browse`, {
    method: "POST",
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function getOpcUaBrowseStatus(dataSourceId: number, signal?: AbortSignal): Promise<OpcUaBrowseStatus> {
  return apiRequest<OpcUaBrowseStatus>(`/api/datasources/${dataSourceId}/tags/browse`, {
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

export function cancelOpcUaBrowse(dataSourceId: number, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/datasources/${dataSourceId}/tags/browse`, {
    method: "DELETE",
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

export function startOpcUaXmlImport(
  dataSourceId: number,
  file: File,
  onProgress?: (loaded: number, total: number) => void,
  signal?: AbortSignal
): Promise<OpcUaBrowseStatus> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const form = new FormData();
    form.append("file", file, file.name);

    const onAbort = () => xhr.abort();
    signal?.addEventListener("abort", onAbort, { once: true });

    xhr.upload.onprogress = (event) => {
      if (!event.lengthComputable || !onProgress) {
        return;
      }
      onProgress(Math.min(event.loaded, event.total), event.total);
    };

    xhr.onerror = () => {
      signal?.removeEventListener("abort", onAbort);
      reject(new ApiError("The API is not reachable.", 0));
    };

    xhr.onabort = () => {
      signal?.removeEventListener("abort", onAbort);
      reject(new ApiError("The request was cancelled.", 499, true, false));
    };

    xhr.onload = () => {
      signal?.removeEventListener("abort", onAbort);
      const text = xhr.responseText ?? "";
      let data: unknown = null;
      if (text) {
        try {
          data = JSON.parse(text) as unknown;
        } catch {
          data = null;
        }
      }

      if (xhr.status === 204) {
        resolve(undefined as unknown as OpcUaBrowseStatus);
        return;
      }

      if (xhr.status < 200 || xhr.status >= 300) {
        if (xhr.status === 401) {
          onUnauthorized();
        }
        const message = (data as { error?: string } | null)?.error
          ?? `Request failed (${xhr.status})`;
        reject(new ApiError(message, xhr.status, false, xhr.status === 408));
        return;
      }

      resolve(data as OpcUaBrowseStatus);
    };

    xhr.open("POST", `/api/datasources/${dataSourceId}/tags/import-xml`);
    xhr.send(form);
  });
}

export function getOpcUaXmlImportStatus(dataSourceId: number, signal?: AbortSignal): Promise<OpcUaBrowseStatus> {
  return apiRequest<OpcUaBrowseStatus>(`/api/datasources/${dataSourceId}/tags/import-xml`, {
    signal,
    timeoutMs: READ_TIMEOUT_MS
  });
}

export function cancelOpcUaXmlImport(dataSourceId: number, signal?: AbortSignal): Promise<void> {
  return apiRequest<void>(`/api/datasources/${dataSourceId}/tags/import-xml`, {
    method: "DELETE",
    signal,
    timeoutMs: WRITE_TIMEOUT_MS
  });
}

function buildEventQuery(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === "") {
      continue;
    }

    search.set(key, String(value));
  }

  const query = search.toString();
  return query ? `?${query}` : "";
}

export function getEventLogPage(
  params: {
    from?: number;
    to?: number;
    level?: string;
    kind?: string;
    sourceId?: number;
    sourceIds?: string;
    component?: string;
    state?: string;
    classes?: string;
    q?: string;
    limit?: number;
    cursor?: string;
    offset?: number;
    sort1?: string;
    dir1?: "asc" | "desc";
  },
  signal?: AbortSignal,
  onProgress?: (progress: QueryProgress) => void
): Promise<EventLogPage> {
  return runQueryJob<EventLogPage>("events/page", buildEventQuery(params), {
    signal,
    label: params.sort1 ? "Sorting alarms" : "Loading alarms",
    onProgress
  });
}

export function getEventLogCount(
  params: {
    from?: number;
    to?: number;
    level?: string;
    kind?: string;
    sourceId?: number;
    sourceIds?: string;
    component?: string;
    state?: string;
    classes?: string;
    q?: string;
  },
  signal?: AbortSignal,
  onProgress?: (progress: QueryProgress) => void
): Promise<EventLogCount> {
  return runQueryJob<EventLogCount>("events/count", buildEventQuery(params), {
    signal,
    label: "Counting alarms",
    onProgress
  });
}

export function getEventLogSummary(
  params: {
    from?: number;
    to?: number;
    level?: string;
    kind?: string;
    sourceId?: number;
    sourceIds?: string;
    component?: string;
    state?: string;
  },
  signal?: AbortSignal,
  onProgress?: (progress: QueryProgress) => void
): Promise<EventLogSummary> {
  return runQueryJob<EventLogSummary>("events/summary", buildEventQuery(params), {
    signal,
    label: "Summarizing alarms",
    onProgress
  });
}

export function getActiveAlarms(
  params: { sourceIds?: string },
  signal?: AbortSignal
): Promise<EventLogPage> {
  return apiRequest<EventLogPage>(`/api/events/active${buildEventQuery(params)}`, { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function getAlarmClasses(signal?: AbortSignal): Promise<AlarmClassItem[]> {
  return apiRequest<AlarmClassItem[]>("/api/alarm-classes", { signal, timeoutMs: READ_TIMEOUT_MS });
}

export function getAlarmStates(
  params: { from?: number; to?: number; sourceIds?: string; classes?: string },
  signal?: AbortSignal
): Promise<string[]> {
  return runQueryJob<string[]>("alarm-states", buildEventQuery(params), {
    signal,
    label: "Reading alarm states",
    background: true
  });
}

export function getAlarmHistory(
  params: { correlationId: string; sourceId?: number; from?: number; to?: number },
  signal?: AbortSignal
): Promise<EventLogPage> {
  return runQueryJob<EventLogPage>("events/alarm-history", buildEventQuery(params), {
    signal,
    label: "Loading alarm history"
  });
}

/** How often the alarm of an occurrence entered a state, per bucket between the edges (Unix ms on full hours). */
export function getAlarmCounts(
  params: { correlationId: string; sourceId?: number; time: number; state: string; edges: number[] },
  signal?: AbortSignal
): Promise<AlarmCounts> {
  return runQueryJob<AlarmCounts>("events/alarm-counts", buildEventQuery({ ...params, edges: params.edges.join(",") }), {
    signal,
    label: "Counting the alarm"
  });
}
