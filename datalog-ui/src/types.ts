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

export type DataSourceType = "System" | "OpcUa" | "S7CommPlus" | "Test" | string;

export interface DataSourceListItem {
  id: number;
  name: string;
  type: DataSourceType;
  typeId: number;
  state: string;
  stateId: number;
  host: string;
  port: number;
  isActive: boolean;
  isSys: boolean;
  opcUaAlarmsEnabled: boolean;
  s7AlarmsEnabled: boolean;
  // Device clock minus DataLogXE clock in ms while connected (positive: the device is ahead), null when unknown
  clockOffsetMs: number | null;
  // Clock offset above which the list warns, 0 = off
  clockWarnMs: number;
  createdAt: string;
  tagBrowseActive: boolean;
  tagImportActive: boolean;
  valuesPerSec: number | null;
  valuesPerSecSeries: number[];
  // OPC UA: the message security, empty for other types
  opcUaSecurityMode: string;
  // OPC UA: the last connect failed because the server showed another than the trusted certificate
  opcUaCertificateRejected: boolean;
}

export interface ArchiveSettings {
  valuesPerSec: number | null;
  valueCountSumMio: number | null;
  databaseDiskSizeGb: number | null;
  statsUpdatedAt: string | null;
  databaseVersion: string | null;
  retentionSeconds: number | null;
}

export interface Dashboard {
  total: number;
  connected: number;
  error: number;
  inactive: number;
  // Active tags of all active data sources, null when the count failed
  activeTags: number | null;
  valuesPerSec: number | null;
  valueCountSumMio: number | null;
  databaseDiskSizeGb: number | null;
  statsUpdatedAt: string | null;
  systemLoadPercent: number | null;
  systemLoadSourceId: number | null;
  systemLoadSourceName: string | null;
  systemLoadTagId: number | null;
  databaseDiskSizeTagId: number | null;
  valueCountSumTagId: number | null;
  valueCountPerSecTagId: number | null;
  // Samples of the last 10 minutes, one per 10 s, oldest first
  valuesPerSecSeries: number[];
  systemLoadSeries: number[];
  // Database size now minus a week ago, null while the archive is younger than a week
  databaseGrowthGb7d: number | null;
  pendingAlarms: number;
  unackedAlarms: number;
  // Alarms that came in the last 24 hours, null when the count failed
  incomingAlarms24h: number | null;
  sources: DataSourceListItem[];
}

export interface DataSourceDetail {
  id: number;
  name: string;
  type: DataSourceType;
  typeId: number;
  isActive: boolean;
  isSys: boolean;
  host: string;
  port: number;
  user: string;
  hasPassword: boolean;
  minCycle: number;
  testTagCount: number;
  testCycleMs: number;
  opcUaAlarmsEnabled: boolean;
  opcUaLocale: string;
  opcUaAvailableLocales: string[];
  s7AlarmsEnabled: boolean;
  s7Locale: string;
  clockWarnMs: number;
  // Whether the device clock is watched (badge and system alarm beyond clockWarnMs)
  clockMonitor: boolean;
  // Whether value and alarm times are moved by the clock difference; false takes the device times as they are
  clockCompensate: boolean;
  opcUaSecurityMode: OpcUaSecurityMode | "";
  opcUaSecurityPolicy: string;
  opcUaCertTrust: OpcUaCertTrust | "";
  opcUaServerCertThumbprint: string;
  // Subject and end of validity of the trusted certificate, as taken over in the connection test
  opcUaServerCertSubject: string;
  opcUaServerCertNotAfterUtc: string | null;
  createdAt: string;
}

export type OpcUaSecurityMode = "none" | "sign" | "signandencrypt";
export type OpcUaCertTrust = "any" | "pinned";

export interface OpcUaEndpointInfo {
  securityMode: string;
  securityPolicy: string;
  securityLevel: number;
  endpointUrl: string;
}

export interface OpcUaCertificateInfo {
  subject: string;
  issuer: string;
  thumbprint: string;
  notBeforeUtc: string;
  notAfterUtc: string;
  selfSigned: boolean;
  applicationUri: string;
}

export interface DataSourceWrite {
  name: string;
  type: string;
  isActive: boolean;
  host: string;
  port: number;
  user: string;
  password: string;
  minCycle: number | null;
  testTagCount: number;
  testCycleMs: number;
  opcUaAlarmsEnabled: boolean;
  opcUaLocale: string;
  opcUaAvailableLocales: string[];
  s7AlarmsEnabled: boolean;
  s7Locale: string;
  clockWarnMs: number | null;
  clockMonitor: boolean | null;
  clockCompensate: boolean | null;
  opcUaSecurityMode: OpcUaSecurityMode | null;
  opcUaSecurityPolicy: string | null;
  opcUaCertTrust: OpcUaCertTrust | null;
  opcUaServerCertThumbprint: string | null;
  opcUaServerCertSubject: string | null;
  opcUaServerCertNotAfterUtc: string | null;
}

export interface OpcUaConnectionTest {
  dataSourceId: number | null;
  host: string;
  port: number;
  user: string;
  password: string;
  opcUaSecurityMode?: OpcUaSecurityMode;
  opcUaSecurityPolicy?: string;
  opcUaCertTrust?: OpcUaCertTrust;
  opcUaServerCertThumbprint?: string;
}

export interface OpcUaConnectionTestResult {
  success: boolean;
  error: string;
  productName: string;
  locales: string[];
  minSampleRate: number | null;
  // Server clock minus DataLogXE clock in ms, null when unknown
  clockOffsetMs: number | null;
  endpointUrl: string;
  securityPolicy: string;
  securityMode: string;
  userIdentity: string;
  applicationName: string;
  applicationUri: string;
  manufacturerName: string;
  softwareVersion: string;
  buildNumber: string;
  serverState: string;
  startTimeUtc: string | null;
  namespaceCount: number | null;
  // Operation limits, 0 = no limit, null = not reported
  maxNodesPerRead: number | null;
  maxNodesPerBrowse: number | null;
  maxMonitoredItemsPerCall: number | null;
  currentSessionCount: number | null;
  currentSubscriptionCount: number | null;
  // The Server object can be subscribed to for events (alarms and conditions)
  eventsSupported: boolean | null;
  // The endpoints the server offers, empty when they could not be read
  endpoints: OpcUaEndpointInfo[];
  // The certificate of the endpoint the session uses
  serverCertificate: OpcUaCertificateInfo | null;
  // What the user must decide before the test signs in, empty when nothing
  certificateDecision: OpcUaCertificateDecision | "";
  // The server refused the secure channel: it does not trust the client certificate of DataLogXE yet
  clientNotTrusted: boolean;
  // The server does not allow an anonymous session: the source needs a user name and password
  signInRequired: boolean;
}

// new: no certificate trusted yet; changed: the server shows another than the trusted one;
// unsecuredonly: the server offers no secured endpoint
export type OpcUaCertificateDecision = "new" | "changed" | "unsecuredonly";

export interface S7ConnectionTestResult {
  success: boolean;
  error: string;
  cpuFamily: string;
  orderNumber: string;
  firmware: string;
  // 1 full access, 2 read access, 3 HMI access, 4 no access (without password)
  protectionLevel: number;
  subscriptionsMax: number;
  subscriptionsFree: number;
  // Each subscribed value uses one attribute of the CPU, shared by all clients.
  attributesMax: number;
  attributesFree: number;
  dataBlockCount: number;
  // Running DataLogXE sources on this PLC and the subscriptions / values they hold
  dataLogXeSources: number;
  dataLogXeSubscriptions: number;
  dataLogXeValues: number;
  // The PLC takes an alarm subscription; alarms it holds right now (-1 unknown)
  alarmsSupported: boolean;
  activeAlarmCount: number;
  // PLC clock minus DataLogXE clock in ms, null when unknown
  clockOffsetMs: number | null;
}

export interface ApiErrorBody {
  error?: string;
}

export interface DataTagListItem {
  id: number;
  name: string;
  displayName: string;
  address: string;
  // Comment from the source (S7: PLC comment) in the language of the source, empty when not translated
  comment?: string;
  cycle: number;
  isActive: boolean;
  isSys: boolean;
}

export interface DataTagListPage {
  dataSourceId: number;
  dataSourceName: string;
  dataSourceState: string;
  dataSourceStateId: number;
  cycles: number[];
  dataSourceType: DataSourceType | string;
  total: number;
  offset: number;
  limit: number;
  preparing?: boolean;
  preparePercent?: number;
  items: DataTagListItem[];
}

export type DataTagValueState = "Invalid" | "Valid" | "Resend";

export interface DataTagActualValue {
  id: number;
  value: number;
  time: string;
  state: DataTagValueState | number | string;
  // Problem of the tag reported by the running source (S7CommPlus: not resolved, PLC limit ...)
  issue?: string;
}

export interface DataTagIssue {
  id: number;
  issue: string;
}

export interface DataTagActualValues {
  dataSourceState: string;
  dataSourceStateId: number;
  values: DataTagActualValue[];
  issues?: DataTagIssue[];
}

export interface DataTagDetails {
  id: number;
  guid: string;
  name: string;
  displayName: string;
  address: string;
  comment?: string;
  type: "Bool" | "Number" | "Unknown" | string;
  cycle: number;
  isActive: boolean;
  isSys: boolean;
  createdAt: string | null;
}

export interface DataTagDetailsList {
  cycles: number[];
  items: DataTagDetails[];
}

export interface DataTagMutationResult {
  updated: DataTagListItem[];
  // Selected tags left inactive because they cannot be logged (strings and system types of an S7 source)
  notActivated?: number;
}

export interface PlotPoints {
  dataTagId: number;
  name: string;
  isBool: boolean;
  compression: string;
  compressionSeconds: number;
  // Pixel width in seconds when the server reduced the points to the M4 points per pixel, else 0
  reductionSeconds: number;
  archiveStart: number | null;
  times: number[];
  raw: (number | null)[];
}

export interface OpcUaBrowseStatusLine {
  text: string;
  replaceLast: boolean;
  // Progress of a running line from 0 to 100, null when it is not known
  percent?: number | null;
}

export interface OpcUaBrowseStatus {
  running: boolean;
  paused: boolean;
  completed: boolean;
  error: string | null;
  lines: OpcUaBrowseStatusLine[];
}

export interface AlarmClassItem {
  name: string;
  symbol: string;
}

export interface EventLogItem {
  time: string;
  level: string;
  kind: string;
  component: string;
  sourceId: number;
  sourceType: string;
  message: string;
  template: string;
  exception: string;
  props: string;
  correlationId: string;
  alarmState: string;
  severity?: number | null;
  active?: boolean | null;
  acked?: boolean | null;
  retain?: boolean | null;
  activeSince?: string | null;
  eventType?: string | null;
  eventTypeName?: string | null;
  alarmClassSymbol?: string | null;
  alarmClassName?: string | null;
  textColor?: number | null;
  backColor?: number | null;
  flashing?: boolean | null;
  durationMs?: number | null;
}

/** How often one alarm entered one state, per bucket between the edges. */
export interface AlarmCounts {
  edges: string[];
  counts: number[];
  /** Set while the alarms from before the counting are still counted: the counts before it are not complete. */
  completeFrom?: string | null;
  /** The oldest stored alarm: the statistics go back no further. Null for none. */
  oldest?: string | null;
}

export interface EventLogPage {
  from: string;
  to: string;
  limit: number;
  hasMore: boolean;
  nextCursor: string | null;
  /** Sorted alarms the table can scroll; null for the storage order. */
  total?: number | null;
  /** Alarms in a sorted range; larger than total when only the first ones of the order are sorted. */
  sortedOf?: number | null;
  items: EventLogItem[];
}

export type QueryJobState = "queued" | "running" | "done" | "failed" | "cancelled";

/** State of a database read that runs as a background job on the server. */
/** What a query job costs the database; it waits for a slot of its class. */
export type QueryCost = "small" | "medium" | "large";

export interface QueryJobStatus<T> {
  id: string;
  state: QueryJobState;
  elapsedMs: number;
  phase: string | null;
  percent: number | null;
  count: number | null;
  cost: QueryCost;
  error: string | null;
  errorStatus: number | null;
  result: T | null;
}

export interface QueryProgress {
  state: QueryJobState;
  phase: string;
  /** 0..100 when the share of the work is known. */
  percent: number | null;
  /** Rows seen so far, for example the alarms counted up to now. */
  count: number | null;
  elapsedMs: number;
}

export interface EventLogCount {
  total: number;
}

export interface EventLogSummary {
  from: string;
  to: string;
  total: number;
  activeAlarms: number;
  countByLevel: Record<string, number>;
}


export type ChartItemType = "Root" | "Folder" | "Chart";

export interface ChartTreeItem {
  id: number;
  name: string;
  type: ChartItemType;
  parentId: number;
  plotCount: number;
  /** The place among the children of the parent, from 0. */
  sortOrder: number;
}

export interface ChartTree {
  maxPlots: number;
  maxNodes: number;
  items: ChartTreeItem[];
}

export interface ChartPlot {
  id: number;
  dataTagId: number;
  dataSourceId: number;
  sourceName: string;
  tagName: string;
  displayName: string;
  isBool: boolean;
  isActive: boolean;
  isSys: boolean;
  isDeleted: boolean;
}

export interface ChartDetail {
  id: number;
  name: string;
  parentId: number;
  plots: ChartPlot[];
}

export interface ChartCreate {
  name: string;
  type: "Folder" | "Chart";
  parentId: number;
  dataTagIds?: number[];
}

/** Fields left out stay as they are. */
export interface ChartUpdate {
  name?: string;
  parentId?: number;
  dataTagIds?: number[];
}

export interface ChartPlotsAppendResult {
  chart: ChartDetail;
  added: number;
  skipped: number;
}

export type UserRole = "admin" | "viewer";

export interface AuthUser {
  id: number;
  name: string;
  role: UserRole;
}

export interface AuthStatus {
  // No user exists yet: the first visitor creates the administrator
  setupRequired: boolean;
  user: AuthUser | null;
}

export interface AppUser {
  id: number;
  name: string;
  role: UserRole;
  createdAt: string;
  lastLoginAt: string | null;
}

export interface AppUserWrite {
  name?: string;
  role?: UserRole;
  password?: string;
}

export interface SourceLogItem {
  // UTC with 100 ns; also the cursor (before/after) of the next read
  time: string;
  level: string;
  component: string;
  message: string;
  exception?: string;
  props?: string;
  // Only in the system log
  sourceId?: number;
  sourceType?: string;
  correlationId?: string;
}

/** The filters and cursor of a read of an application log (data source log or system log). */
export interface AppLogParams {
  before?: string;
  after?: string;
  level?: string;
  q?: string;
  limit?: number;
  component?: string;
  source?: string | number;
  correlation?: string;
}

export interface SourceLogPage {
  items: SourceLogItem[];
  // More events than the limit: a live read missed some, a page goes on at nextBefore
  truncated: boolean;
  // Cursor of the next older page; null at the end of the retention
  nextBefore: string | null;
}

export interface SourceLogCount {
  sourceId: number;
  warnings: number;
  errors: number;
}

// A line of the log of a tag job dialog (browse, XML import)
export interface TagJobLogLine {
  text: string;
  success?: boolean;
  active?: boolean;
  // Progress of the active line from 0 to 100, null when it is not known
  percent?: number | null;
}

// A badge or hint in the status row of a tag job dialog
export interface TagJobStatus {
  kind: "running" | "warn" | "error" | "hint";
  text: string;
}
