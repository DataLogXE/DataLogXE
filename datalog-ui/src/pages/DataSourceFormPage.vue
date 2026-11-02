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
    <div class="page-header">
      <h1>{{ isNew ? "New data source" : "Edit data source" }}</h1>
      <router-link class="btn secondary" to="/datasources">Back</router-link>
    </div>

    <div class="page-body">
    <form class="form-card source-form" @submit.prevent="submit">
      <section class="source-form-section">
        <h2>General</h2>
        <div class="source-form-grid cols-3">
          <div class="field">
            <label for="nameInput">Name</label>
            <input
              id="nameInput"
              v-model="form.name"
              maxlength="100"
              autocomplete="off"
              spellcheck="false"
              :disabled="form.isSys || leaving"
            />
          </div>
          <div class="field">
            <label for="typeSelect">Type</label>
            <SourceTypeSelect
              id="typeSelect"
              v-model="form.type"
              :options="typeOptions"
              :disabled="!isNew || form.isSys || leaving"
            />
          </div>
          <!-- Next to Type, as high as the fields and on their line -->
          <label class="source-check source-check-inline">
            <input v-model="form.isActive" type="checkbox" :disabled="form.isSys || leaving" />
            <span>
              <span class="source-check-title">
                Active<HelpTip text="The source connects and collects values while it is active." />
              </span>
            </span>
          </label>
        </div>
      </section>

      <section v-if="showConnectionFields" class="source-form-section">
        <div class="source-tabs" role="tablist" aria-label="Connection settings">
          <button
            v-for="tab in connectionTabs"
            :id="`source-tab-${tab.id}`"
            :key="tab.id"
            type="button"
            role="tab"
            class="source-tab"
            :class="{ active: activeTab === tab.id }"
            :aria-selected="activeTab === tab.id"
            :aria-controls="`source-panel-${tab.id}`"
            @click="activeTab = tab.id"
          >
            {{ tab.label }}
            <span v-if="tab.warn" class="source-tab-dot" data-hint="Needs attention" aria-label="needs attention"></span>
          </button>
        </div>

        <div
          v-show="activeTab === 'connection'"
          id="source-panel-connection"
          class="source-tab-panel"
          role="tabpanel"
          aria-labelledby="source-tab-connection"
        >
          <div class="source-form-grid" :class="{ 'cols-3': showOpcFields || isS7 }">
            <div class="field">
              <label for="hostInput">
                IP address / host<HelpTip v-if="isS7" text="S7-1200 / S7-1500 with TLS (TIA Portal V17 or later)." />
              </label>
              <input
                id="hostInput"
                ref="hostInput"
                v-model="form.host"
                maxlength="100"
                autocomplete="off"
                spellcheck="false"
                :disabled="form.isSys || leaving"
              />
            </div>
            <div v-if="showOpcFields" class="field">
              <label for="portInput">Port</label>
              <input
                id="portInput"
                v-model.number="form.port"
                type="number"
                min="1024"
                max="65535"
                :disabled="form.isSys || leaving"
              />
            </div>
            <div v-if="isS7" class="field">
              <label for="s7LocaleSelect">
                Language<HelpTip
                  text="The texts of the PLC project in this language: the comments of the variables and the alarm texts. A text that is not translated into it stays empty. The browse keeps all languages, a change needs no new browse."
                />
              </label>
              <select id="s7LocaleSelect" v-model="form.s7Locale" :disabled="form.isSys || leaving">
                <option v-for="option in s7Languages" :key="option.value" :value="option.value">
                  {{ option.label }}
                </option>
              </select>
            </div>
            <div v-if="showOpcFields" class="field">
              <label for="localeSelect">Locale<HelpTip :text="localeHint" /></label>
              <select id="localeSelect" v-model="form.opcUaLocale" :disabled="form.isSys || leaving">
                <option value="">Server default</option>
                <option v-for="locale in localeOptions" :key="locale" :value="locale">{{ locale }}</option>
              </select>
            </div>
          </div>

          <template v-if="showOpcFields">
            <h3 class="source-subhead">
              Security<HelpTip
                text="Sign &amp; Encrypt protects the values and the password on the way. The server must trust the client certificate of DataLogXE (Settings)."
              />
            </h3>
            <div class="source-form-grid cols-3">
              <div class="field">
                <label for="securityModeSelect">Mode</label>
                <select id="securityModeSelect" v-model="form.opcUaSecurityMode" :disabled="form.isSys || leaving">
                  <option value="signandencrypt">Sign &amp; Encrypt</option>
                  <option value="sign">Sign</option>
                  <option value="none">None (unsecured)</option>
                </select>
              </div>
              <div class="field">
                <label for="securityPolicySelect">
                  Security policy<HelpTip text="Best available takes the strongest policy the server offers." />
                </label>
                <select
                  id="securityPolicySelect"
                  v-model="form.opcUaSecurityPolicy"
                  :disabled="form.isSys || leaving || form.opcUaSecurityMode === 'none'"
                >
                  <option value="best">Best available</option>
                  <option v-for="policy in securityPolicies" :key="policy" :value="policy">{{ policy }}</option>
                </select>
              </div>
              <div class="field">
                <span class="field-label" id="certStatusLabel">Server certificate<HelpTip :text="certHelp" /></span>
                <div
                  class="cert-status"
                  :class="`is-${certStatus.tone}`"
                  role="status"
                  aria-labelledby="certStatusLabel"
                  :data-hint="form.opcUaServerCertThumbprint ? `Thumbprint ${groupThumbprint(form.opcUaServerCertThumbprint)}` : undefined"
                >
                  <span class="cert-status-text">{{ certStatus.text }}</span>
                  <button
                    v-if="certStatus.action === 'test'"
                    type="button"
                    class="cert-status-action"
                    :disabled="form.isSys || busy || leaving"
                    @click="checkCertificate"
                  >
                    Check
                  </button>
                  <button
                    v-else-if="certStatus.action === 'remove'"
                    type="button"
                    class="cert-status-action cert-status-remove"
                    data-hint="Remove the trust in this certificate"
                    aria-label="Remove the trust in this certificate"
                    :disabled="form.isSys || busy || leaving"
                    @click="removeTrust"
                  >
                    ×
                  </button>
                </div>
              </div>
            </div>
            <label class="source-check">
              <input v-model="form.signIn" type="checkbox" :disabled="form.isSys || leaving" />
              <span>
                <span class="source-check-title">
                  Sign in with a user<HelpTip
                    text="Off, the session is anonymous. On, DataLogXE signs in with this user and password; the password is stored encrypted."
                  />
                </span>
              </span>
            </label>
            <div v-if="form.signIn" class="source-form-grid cols-3 source-dependent">
              <div class="field">
                <label for="userInput">Username</label>
                <input
                  id="userInput"
                  ref="userInput"
                  v-model="form.user"
                  maxlength="100"
                  autocomplete="off"
                  spellcheck="false"
                  :disabled="form.isSys || leaving"
                />
              </div>
              <div class="field">
                <label for="passInput">Password</label>
                <input
                  id="passInput"
                  v-model="form.password"
                  type="password"
                  maxlength="100"
                  autocomplete="off"
                  :placeholder="passwordPlaceholder"
                  :disabled="form.isSys || leaving"
                />
              </div>
            </div>
          </template>
        </div>

        <div
          v-show="activeTab === 'clock'"
          id="source-panel-clock"
          class="source-tab-panel"
          role="tabpanel"
          aria-labelledby="source-tab-clock"
        >
          <label class="source-check">
            <input v-model="form.clockMonitor" type="checkbox" :disabled="form.isSys || leaving" />
            <span>
              <span class="source-check-title">
                Monitor the device clock<HelpTip
                  text="A device clock off by more than the accepted difference is shown on the data sources page and raises a system alarm."
                />
              </span>
            </span>
          </label>
          <div class="source-form-grid cols-3 source-dependent">
            <div class="field">
              <label for="clockWarnSelect">
                Accepted clock difference<HelpTip
                  text="Beyond this, the data sources page shows the source in orange and a system alarm is raised."
                />
              </label>
              <select
                id="clockWarnSelect"
                v-model.number="form.clockWarnMs"
                :disabled="form.isSys || leaving || !form.clockMonitor"
              >
                <option v-for="option in clockWarnOptions" :key="option.value" :value="option.value">
                  {{ option.label }}
                </option>
              </select>
            </div>
          </div>
          <span v-if="clockTestWarning" class="source-field-hint source-check-warning">
            The last connection test measured {{ clockTestWarning }}, beyond the accepted difference.
          </span>
          <label class="source-check">
            <input v-model="form.clockCompensate" type="checkbox" :disabled="form.isSys || leaving" />
            <span>
              <span class="source-check-title">
                Correct time stamps by the clock difference<HelpTip
                  text="Values and alarms get the time of DataLogXE. Off, the time stamps of the device are taken as they are: a device clock that is ahead puts entries into the future and delays the times of the following alarms of all sources."
                />
              </span>
            </span>
          </label>
        </div>

        <div
          v-show="activeTab === 'alarms'"
          id="source-panel-alarms"
          class="source-tab-panel"
          role="tabpanel"
          aria-labelledby="source-tab-alarms"
        >
          <label v-if="showOpcFields" class="source-check">
            <input v-model="form.opcUaAlarmsEnabled" type="checkbox" :disabled="form.isSys || leaving" />
            <span>
              <span class="source-check-title">
                Collect alarms and conditions<HelpTip text="OPC UA alarms and conditions are written to the event log." />
              </span>
            </span>
          </label>
          <template v-if="isS7">
            <label class="source-check">
              <input v-model="form.s7AlarmsEnabled" type="checkbox" :disabled="form.isSys || leaving" />
              <span>
                <span class="source-check-title">
                  Receive PLC alarms<HelpTip
                    text="Program alarms, system diagnostics and security messages of the PLC are written to the event log (one PLC subscription)."
                  />
                </span>
                <span v-if="s7DuplicateSource" class="source-check-hint source-check-warning">
                  The OPC UA source "{{ s7DuplicateSource }}" already collects the alarms of this PLC: its alarms will
                  be logged twice.
                </span>
              </span>
            </label>
          </template>
        </div>
      </section>

      <section v-if="showTestFields" class="source-form-section">
        <h2>Test</h2>
        <div class="source-form-grid">
          <div class="field">
            <label for="testTagCount">Test tags per type (28 types)</label>
            <input
              id="testTagCount"
              v-model.number="form.testTagCount"
              type="number"
              min="1"
              max="1000"
              :disabled="form.isSys || leaving"
            />
          </div>
          <div class="field">
            <label for="testCycleMs">Initial cycle [ms]</label>
            <select id="testCycleMs" v-model.number="form.testCycleMs" :disabled="form.isSys || leaving">
              <option v-for="cycle in testCycles" :key="cycle" :value="cycle">{{ cycle }}</option>
            </select>
          </div>
        </div>
      </section>

      <p v-if="form.isSys" class="source-form-note hint">The system data source is read-only.</p>
      <p v-if="error" class="source-form-note error-text">{{ error }}</p>

      <div class="source-form-actions">
        <button
          v-if="showConnectionFields"
          class="btn secondary source-form-test"
          type="button"
          :disabled="form.isSys || busy || leaving"
          @click="runConnectionTest()"
        >
          Connection test
        </button>
        <div class="source-form-buttons">
          <button
            v-if="!isNew && !form.isSys"
            class="btn danger"
            type="button"
            :disabled="busy || leaving"
            @click="deleteOpen = true"
          >
            Delete
          </button>
          <button class="btn secondary" type="button" :disabled="busy || leaving" @click="resetForm">Reset</button>
          <button class="btn" type="submit" :disabled="form.isSys || busy || leaving">
            {{ isNew ? "Create" : "Save" }}
          </button>
        </div>
      </div>
    </form>
    </div>

    <ConfirmDialog
      v-if="deleteOpen"
      title="Delete data source"
      :message="`Delete data source &quot;${form.name}&quot;?`"
      confirm-label="Delete"
      :busy="false"
      error=""
      @confirm="confirmDelete"
      @close="deleteOpen = false"
    />

    <WaitOverlay
      :visible="busy"
      :title="overlayTitle"
      :message="overlayMessage"
      :error="overlayError"
      :cancellable="true"
      @cancel="cancelCurrent"
    />

    <div v-if="opcResult" class="overlay s7-result-overlay" @click.self="opcResult = null">
      <div class="overlay-card s7-result-card opc-result-card" role="dialog" aria-modal="true" aria-labelledby="opc-result-title">
        <strong id="opc-result-title">Connection test: {{ opcResult.success ? "connected" : "failed" }}</strong>
        <p v-if="!opcResult.success" class="error-text opc-result-error">{{ opcResult.error }}</p>

        <!-- Two blocks per row, so the blocks of a row start on the same line -->
        <div class="opc-result-columns">
          <table class="s7-result-table">
            <tbody>
              <tr><th colspan="2">Server</th></tr>
              <tr>
                <td>Product</td>
                <td>
                  {{ opcResult.productName || "–" }}<template v-if="opcResult.manufacturerName">
                    ({{ opcResult.manufacturerName }})</template>
                </td>
              </tr>
              <tr>
                <td>Version</td>
                <td>{{ opcResult.softwareVersion || "–" }}<template v-if="opcResult.buildNumber"> build {{ opcResult.buildNumber }}</template></td>
              </tr>
              <tr>
                <td>Application</td>
                <td :data-hint="opcResult.applicationUri || undefined">{{ opcResult.applicationName || opcResult.applicationUri || "–" }}</td>
              </tr>
              <tr>
                <td>State</td>
                <td>
                  {{ opcResult.serverState || "–" }}<template v-if="opcResult.startTimeUtc">
                    since {{ formatDateTime(new Date(opcResult.startTimeUtc)) }}</template>
                </td>
              </tr>
              <tr>
                <td>Clock</td>
                <td :class="{ 'clock-off': clockBeyondLimit(opcResult.clockOffsetMs) }">
                  {{ opcResult.clockOffsetMs == null ? "not reported" : formatClockOffset(opcResult.clockOffsetMs) }}
                </td>
              </tr>
            </tbody>
          </table>
          <table class="s7-result-table">
            <tbody>
              <tr><th colspan="2">Capabilities</th></tr>
              <tr><td>Locales</td><td>{{ opcResult.locales.length ? opcResult.locales.join(", ") : "none" }}</td></tr>
              <tr>
                <td>Min. sample rate</td>
                <td>{{ opcResult.minSampleRate != null ? `${opcResult.minSampleRate} ms` : "–" }}</td>
              </tr>
              <tr><td>Namespaces</td><td>{{ opcResult.namespaceCount ?? "–" }}</td></tr>
              <tr>
                <td data-hint="Max. nodes per read / per browse / monitored items per call">Limits</td>
                <td data-hint="read / browse / monitored items per call">
                  {{ limitText(opcResult.maxNodesPerRead) }} / {{ limitText(opcResult.maxNodesPerBrowse) }} /
                  {{ limitText(opcResult.maxMonitoredItemsPerCall) }}
                </td>
              </tr>
              <tr>
                <td>Alarms</td>
                <td>{{ opcResult.eventsSupported == null ? "–" : opcResult.eventsSupported ? "supported" : "not supported" }}</td>
              </tr>
            </tbody>
          </table>
          <table class="s7-result-table">
            <tbody v-if="opcResult.endpoints.length">
              <tr><th colspan="2">Offered endpoints</th></tr>
              <tr v-for="(endpoint, index) in opcResult.endpoints" :key="index">
                <td>{{ endpoint.securityMode }}</td>
                <td>{{ endpoint.securityPolicy }}</td>
              </tr>
            </tbody>
          </table>
          <table class="s7-result-table">
            <tbody v-if="opcResult.serverCertificate">
              <tr><th colspan="2">Server certificate</th></tr>
              <tr><td>Subject</td><td>{{ opcResult.serverCertificate.subject }}</td></tr>
              <tr>
                <td>Thumbprint</td>
                <td class="cert-thumbprint">{{ opcResult.serverCertificate.thumbprint }}</td>
              </tr>
              <tr>
                <td>Valid until</td>
                <td>
                  {{ formatDateTime(new Date(opcResult.serverCertificate.notAfterUtc)) }}
                  <template v-if="opcResult.serverCertificate.selfSigned"> · self-signed</template>
                </td>
              </tr>
              <tr>
                <td>Checked</td>
                <td>{{ certCheckText }}</td>
              </tr>
            </tbody>
          </table>
          <table class="s7-result-table">
            <tbody>
              <tr><th colspan="2">Connection</th></tr>
              <tr><td>Endpoint</td><td>{{ opcResult.endpointUrl || "–" }}</td></tr>
              <tr>
                <td>Security</td>
                <td>{{ opcResult.securityPolicy || "–" }}<template v-if="opcResult.securityMode"> · {{ opcResult.securityMode }}</template></td>
              </tr>
              <tr><td>User</td><td>{{ opcResult.userIdentity || "–" }}</td></tr>
            </tbody>
          </table>
          <table class="s7-result-table">
            <tbody>
              <tr><th colspan="2">Load</th></tr>
              <tr><td>Sessions</td><td>{{ opcResult.currentSessionCount ?? "–" }}</td></tr>
              <tr><td>Subscriptions</td><td>{{ opcResult.currentSubscriptionCount ?? "–" }}</td></tr>
            </tbody>
          </table>
        </div>

        <div class="s7-result-actions">
          <button class="btn" type="button" @click="opcResult = null">Close</button>
        </div>
      </div>
    </div>

    <div v-if="decision" class="overlay s7-result-overlay">
      <div class="overlay-card s7-result-card decision-card" role="dialog" aria-modal="true" aria-labelledby="decision-title">
        <template v-if="decision.kind === 'new' || decision.kind === 'changed'">
          <strong id="decision-title">
            {{ decision.kind === "new" ? "Trust this server?" : "The server certificate has changed" }}
          </strong>
          <p v-if="decision.kind === 'new'" class="decision-text">
            DataLogXE will only connect to a server with this certificate. If you are not sure, compare the thumbprint
            with the certificate of the device.
          </p>
          <p v-else class="decision-text decision-warning">
            This is expected after the certificate of the server was renewed. If it was not, another device may be
            answering at this address: do not trust it.
          </p>
          <table class="s7-result-table">
            <tbody>
              <tr><td>Server</td><td>{{ decision.result.applicationName || "–" }}</td></tr>
              <tr><td>Address</td><td>{{ form.host.trim() }}:{{ Number(form.port) || 4840 }}</td></tr>
              <template v-if="decision.result.serverCertificate">
                <tr><td>Subject</td><td>{{ decision.result.serverCertificate.subject }}</td></tr>
                <tr>
                  <td>Thumbprint</td>
                  <td class="cert-thumbprint">{{ groupThumbprint(decision.result.serverCertificate.thumbprint) }}</td>
                </tr>
                <tr>
                  <td>Valid</td>
                  <td>
                    {{ formatDate(decision.result.serverCertificate.notBeforeUtc) }} –
                    {{ formatDate(decision.result.serverCertificate.notAfterUtc) }}
                    <template v-if="decision.result.serverCertificate.selfSigned"> · self-signed</template>
                  </td>
                </tr>
              </template>
              <tr v-if="decision.kind === 'changed'">
                <td>Trusted until now</td>
                <td class="cert-thumbprint">{{ groupThumbprint(form.opcUaServerCertThumbprint) }}</td>
              </tr>
            </tbody>
          </table>
          <div class="s7-result-actions">
            <button class="btn secondary" type="button" @click="cancelDecision">Cancel</button>
            <button class="btn" type="button" :disabled="!decision.result.serverCertificate" @click="trustCertificate">
              {{ decision.kind === "new" ? "Trust" : "Trust new certificate" }}
            </button>
          </div>
        </template>

        <template v-else-if="decision.kind === 'unsecuredonly'">
          <strong id="decision-title">The server does not support encryption</strong>
          <p class="decision-text decision-warning">
            Values and the password would travel in plain text, and DataLogXE cannot check which device answers.
          </p>
          <div class="s7-result-actions">
            <button class="btn secondary" type="button" @click="cancelDecision">Cancel</button>
            <button class="btn" type="button" @click="connectUnsecured">Connect unsecured</button>
          </div>
        </template>

        <template v-else>
          <strong id="decision-title">The server does not trust DataLogXE yet</strong>
          <p class="decision-text">
            Add the client certificate of DataLogXE to the trusted certificates of the server (for an S7-1500 in TIA
            Portal: OPC UA server &gt; Security &gt; Trusted clients), load the change and test again.
          </p>
          <div class="s7-result-actions">
            <a class="btn secondary" :href="clientCertDownloadUrl" download>Download certificate</a>
            <button class="btn secondary" type="button" @click="decision = null">Close</button>
            <button class="btn" type="button" @click="testAgain">Test again</button>
          </div>
        </template>
      </div>
    </div>

    <div v-if="s7Result" class="overlay s7-result-overlay" @click.self="s7Result = null">
      <div class="overlay-card s7-result-card" role="dialog" aria-modal="true" aria-labelledby="s7-result-title">
        <strong id="s7-result-title">Connection test: connected</strong>

        <table class="s7-result-table">
          <tbody>
            <tr><th colspan="4">CPU</th></tr>
            <tr><td>Family</td><td colspan="3">{{ s7Result.cpuFamily || "–" }}</td></tr>
            <tr><td>Order number</td><td colspan="3">{{ s7Result.orderNumber || "–" }}</td></tr>
            <tr><td>Firmware</td><td colspan="3">{{ s7Result.firmware || "–" }}</td></tr>
            <tr><td>Access without password</td><td colspan="3">{{ protectionText(s7Result.protectionLevel) }}</td></tr>
            <tr><td>Data blocks</td><td colspan="3">{{ s7Result.dataBlockCount }}</td></tr>
            <tr>
              <td>Clock</td>
              <td colspan="3" :class="{ 'clock-off': clockBeyondLimit(s7Result.clockOffsetMs) }">
                {{ s7Result.clockOffsetMs == null ? "not reported" : formatClockOffset(s7Result.clockOffsetMs) }}
              </td>
            </tr>
            <tr><td>Alarm subscription</td><td colspan="3">{{ s7Result.alarmsSupported ? "possible" : "not possible" }}</td></tr>
            <tr v-if="s7Result.activeAlarmCount >= 0">
              <td>Alarms in the PLC</td><td colspan="3">{{ s7Result.activeAlarmCount }}</td>
            </tr>
          </tbody>
          <tbody>
            <tr>
              <th>PLC limits</th>
              <th class="num">Max</th>
              <th class="num">Used</th>
              <th class="num">Free</th>
            </tr>
            <tr>
              <td>Subscriptions</td>
              <td class="num">{{ s7Result.subscriptionsMax }}</td>
              <td class="num">{{ s7Result.subscriptionsMax - s7Result.subscriptionsFree }}</td>
              <td class="num">{{ s7Result.subscriptionsFree }}</td>
            </tr>
            <tr>
              <td>Subscribed values</td>
              <td class="num">{{ s7Result.attributesMax }}</td>
              <td class="num">{{ s7Result.attributesMax - s7Result.attributesFree }}</td>
              <td class="num">{{ s7Result.attributesFree }}</td>
            </tr>
          </tbody>
          <tbody>
            <tr><th colspan="4">Used by DataLogXE</th></tr>
            <tr><td>Data sources on this PLC</td><td colspan="3" class="num">{{ s7Result.dataLogXeSources }}</td></tr>
            <tr><td>Subscriptions</td><td colspan="3" class="num">{{ s7Result.dataLogXeSubscriptions }}</td></tr>
            <tr>
              <td>Subscribed values</td>
              <td colspan="3" class="num">
                {{ s7Result.dataLogXeValues }} of {{ Math.floor(s7Result.attributesMax / 2) }} allowed
              </td>
            </tr>
          </tbody>
        </table>

        <p class="hint">
          Subscriptions and values are shared by all clients of the CPU (HMI, SCADA, DataLogXE). DataLogXE uses at
          most half of the subscribed values, the other half stays free for the other clients.
        </p>
        <div class="s7-result-actions">
          <button class="btn" type="button" @click="s7Result = null">Close</button>
        </div>
      </div>
    </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onUnmounted, reactive, ref, watch } from "vue";
import { useRoute, useRouter } from "vue-router";
import ConfirmDialog from "../components/ConfirmDialog.vue";
import HelpTip from "../components/HelpTip.vue";
import SourceTypeSelect from "../components/SourceTypeSelect.vue";
import WaitOverlay from "../components/WaitOverlay.vue";
import {
  OPC_UA_CLIENT_CERTIFICATE_DOWNLOAD,
  createDataSource,
  deleteDataSource,
  getDataSource,
  getDataSources,
  isAbortError,
  testOpcUaConnection,
  testS7CommPlusConnection,
  updateDataSource
} from "../api";
import { notify } from "../notify";
import { formatClockOffset, formatDateTime } from "../format";
import type {
  DataSourceDetail,
  DataSourceListItem,
  DataSourceWrite,
  OpcUaCertTrust,
  OpcUaCertificateDecision,
  OpcUaConnectionTestResult,
  OpcUaSecurityMode,
  S7ConnectionTestResult
} from "../types";

const route = useRoute();
const router = useRouter();
const isNew = computed(() => route.name === "datasource-new");
const sourceId = computed(() => Number(route.params.id));

const form = reactive({
  name: "",
  type: "OpcUa",
  isActive: true,
  isSys: false,
  host: "",
  port: 4840,
  // Off, the session is anonymous and the user and the password are not saved.
  signIn: false,
  user: "",
  password: "",
  // The min sample rate of the last connection test, saved with the form; null keeps the stored value.
  minCycle: null as number | null,
  testTagCount: 10,
  testCycleMs: 1000,
  opcUaAlarmsEnabled: true,
  opcUaLocale: "",
  opcUaAvailableLocales: [] as string[],
  s7AlarmsEnabled: false,
  s7Locale: "",
  clockWarnMs: 10000,
  clockMonitor: true,
  clockCompensate: true,
  // New sources are secured and pin the certificate of the connection test
  opcUaSecurityMode: "signandencrypt" as OpcUaSecurityMode,
  opcUaSecurityPolicy: "best",
  opcUaCertTrust: "pinned" as OpcUaCertTrust,
  opcUaServerCertThumbprint: "",
  // Shown in the status of the trusted certificate
  opcUaServerCertSubject: "",
  opcUaServerCertNotAfterUtc: null as string | null,
  hasPassword: false
});

const securityPolicies = ["Aes256_Sha256_RsaPss", "Basic256Sha256", "Aes128_Sha256_RsaOaep"];

const clientCertDownloadUrl = OPC_UA_CLIENT_CERTIFICATE_DOWNLOAD;
const dayMs = 24 * 60 * 60 * 1000;

// The running source refused the server certificate: the server shows another than the trusted one.
const certRejected = computed(
  () =>
    !isNew.value &&
    form.opcUaServerCertThumbprint !== "" &&
    form.opcUaServerCertThumbprint === (original.value?.opcUaServerCertThumbprint ?? "") &&
    !!otherSources.value.find((source) => source.id === sourceId.value)?.opcUaCertificateRejected
);

type CertTone = "ok" | "warn" | "danger" | "muted";
const certStatus = computed<{ text: string; tone: CertTone; action: "" | "test" | "remove" }>(() => {
  if (form.opcUaSecurityMode === "none") {
    return { text: "Not checked (unsecured)", tone: "warn", action: "" };
  }
  // Sources from before 0.0.203 may accept any certificate; Check turns them to a trusted one.
  if (form.opcUaCertTrust === "any") {
    return { text: "Not checked", tone: "warn", action: "test" };
  }
  if (!form.opcUaServerCertThumbprint) {
    return { text: "Not trusted yet", tone: "warn", action: "test" };
  }
  if (certRejected.value) {
    return { text: "Changed on the server", tone: "danger", action: "test" };
  }
  const name = commonName(form.opcUaServerCertSubject) || form.opcUaServerCertThumbprint.slice(0, 8);
  const notAfter = form.opcUaServerCertNotAfterUtc ? new Date(form.opcUaServerCertNotAfterUtc) : null;
  if (notAfter && notAfter.getTime() < Date.now()) {
    return { text: `${name} · expired ${formatDate(notAfter)}`, tone: "danger", action: "remove" };
  }
  if (notAfter && notAfter.getTime() - Date.now() < 30 * dayMs) {
    return { text: `${name} · expires ${formatDate(notAfter)}`, tone: "warn", action: "remove" };
  }
  return {
    text: notAfter ? `✓ ${name} · until ${formatDate(notAfter)}` : `✓ ${name}`,
    tone: "ok",
    action: "remove"
  };
});

// The connection tab gets a dot while the server certificate is not checked or not in order.
const securityWarning = computed(() => certStatus.value.tone !== "ok");

const certHelp = computed(() => {
  const text =
    "The connection test shows the certificate of the server and asks whether to trust it. Afterwards DataLogXE " +
    "connects only to a server with this certificate, also with the password.";
  return form.opcUaServerCertThumbprint
    ? `${text} Trusted: ${groupThumbprint(form.opcUaServerCertThumbprint)}.`
    : text;
});

// How the last connection test checked the server certificate
const certCheckText = computed(() => {
  if (!opcResult.value?.success) return "–";
  if (form.opcUaSecurityMode === "none") return "no (unsecured)";
  if (form.opcUaCertTrust === "any") return "no (any certificate accepted)";
  return "yes, the trusted certificate";
});

function commonName(subject: string): string {
  return /(?:^|,\s*)CN=([^,]+)/.exec(subject ?? "")?.[1]?.trim() ?? "";
}

function groupThumbprint(thumbprint: string): string {
  return (thumbprint ?? "").toUpperCase().match(/.{1,4}/g)?.join(" ") ?? "";
}

const dateFormat = new Intl.DateTimeFormat(undefined, { year: "numeric", month: "2-digit", day: "2-digit" });
function formatDate(value: Date | string): string {
  return dateFormat.format(typeof value === "string" ? new Date(value) : value);
}

function removeTrust() {
  form.opcUaServerCertThumbprint = "";
  form.opcUaServerCertSubject = "";
  form.opcUaServerCertNotAfterUtc = null;
}

// The measured offset is beyond the accepted difference chosen in the form, while the clock is watched.
function clockBeyondLimit(ms: number | null | undefined): boolean {
  return ms != null && form.clockMonitor && form.clockWarnMs > 0 && Math.abs(ms) > form.clockWarnMs;
}

const clockWarnOptions = reactive([
  { value: 1000, label: "1 s" },
  { value: 2000, label: "2 s" },
  { value: 5000, label: "5 s" },
  { value: 10000, label: "10 s (default)" },
  { value: 30000, label: "30 s" },
  { value: 60000, label: "1 min" },
  { value: 120000, label: "2 min" },
  { value: 300000, label: "5 min" },
  { value: 600000, label: "10 min" },
  { value: 3600000, label: "1 h" }
]);

const s7Languages = [
  { value: "", label: "English" },
  { value: "de-DE", label: "German (de-DE)" },
  { value: "fr-FR", label: "French (fr-FR)" },
  { value: "it-IT", label: "Italian (it-IT)" },
  { value: "es-ES", label: "Spanish (es-ES)" },
  { value: "nl-NL", label: "Dutch (nl-NL)" },
  { value: "pl-PL", label: "Polish (pl-PL)" },
  { value: "cs-CZ", label: "Czech (cs-CZ)" },
  { value: "hu-HU", label: "Hungarian (hu-HU)" },
  { value: "sv-SE", label: "Swedish (sv-SE)" },
  { value: "pt-BR", label: "Portuguese (pt-BR)" },
  { value: "ru-RU", label: "Russian (ru-RU)" },
  { value: "tr-TR", label: "Turkish (tr-TR)" },
  { value: "zh-CN", label: "Chinese (zh-CN)" },
  { value: "ja-JP", label: "Japanese (ja-JP)" },
  { value: "ko-KR", label: "Korean (ko-KR)" }
];

// The other sources, to warn when an OPC UA source already collects the alarms of the same PLC.
const otherSources = ref<DataSourceListItem[]>([]);
const s7DuplicateSource = computed(() => {
  const host = form.host.trim().toLowerCase();
  if (!isS7.value || !form.s7AlarmsEnabled || !host) {
    return "";
  }
  const match = otherSources.value.find(
    (source) =>
      source.type === "OpcUa" &&
      source.opcUaAlarmsEnabled &&
      source.isActive &&
      source.host.trim().toLowerCase() === host &&
      source.id !== sourceId.value
  );
  return match?.name ?? "";
});



const testCycles = [10, 50, 100, 500, 1000, 5000, 10000];

const typeOptions = computed(() => [
  { value: "OpcUa", label: "OPC UA", hint: "Values and alarms of an OPC UA server" },
  { value: "S7CommPlus", label: "S7CommPlus", hint: "Values and alarms of a Siemens S7‑1200 or S7‑1500" },
  { value: "Test", label: "Test", hint: "Simulated values, without a device" },
  ...(form.type === "System" ? [{ value: "System", label: "System", hint: "The values of DataLogXE itself" }] : [])
]);

const original = ref<DataSourceDetail | null>(null);
const busy = ref(false);
const error = ref("");
const leaving = ref(false);
const overlayError = ref("");
const overlayTitle = ref("Loading");
const overlayMessage = ref("");
let controller: AbortController | null = null;
let redirectTimer: ReturnType<typeof setTimeout> | null = null;
let closed = false;

const showOpcFields = computed(() => form.type === "OpcUa");
const isS7 = computed(() => form.type === "S7CommPlus");
const showConnectionFields = computed(() => showOpcFields.value || isS7.value);

const s7Result = ref<S7ConnectionTestResult | null>(null);
const opcResult = ref<OpcUaConnectionTestResult | null>(null);

type ConnectionTab = "connection" | "clock" | "alarms";
const activeTab = ref<ConnectionTab>("connection");
const hostInput = ref<HTMLInputElement | null>(null);
const userInput = ref<HTMLInputElement | null>(null);

// The clock offset of the last connection test, when it is beyond the accepted difference.
const clockTestWarning = computed(() => {
  const ms = isS7.value ? s7Result.value?.clockOffsetMs : opcResult.value?.clockOffsetMs;
  return clockBeyondLimit(ms) ? formatClockOffset(ms as number) : "";
});

// A dot marks a tab that needs attention. S7 has no security settings: TLS is fixed.
const connectionTabs = computed(() => [
  { id: "connection" as const, label: "Connection", warn: showOpcFields.value && securityWarning.value },
  { id: "clock" as const, label: "Clock", warn: !!clockTestWarning.value },
  { id: "alarms" as const, label: "Alarms", warn: !!s7DuplicateSource.value }
]);

function defaultPort(type: string) {
  return type === "S7CommPlus" ? 102 : 4840;
}

// A new source follows the default port of its type unless the user typed another one.
watch(
  () => form.type,
  (type, previous) => {
    if (isNew.value && (!form.port || form.port === defaultPort(previous ?? ""))) {
      form.port = defaultPort(type);
    }
  }
);

const protectionLevels: Record<number, string> = {
  1: "full access",
  2: "read access",
  3: "HMI access",
  4: "no access"
};

// An operation limit: 0 means the server sets none.
function limitText(value: number | null): string {
  if (value == null) return "–";
  return value === 0 ? "∞" : String(value);
}

function protectionText(level: number) {
  return protectionLevels[level] ?? `level ${level}`;
}
const showTestFields = computed(() => form.type === "Test");
// The stored locale stays selectable even when the server no longer reports it.
const localeOptions = computed(() => {
  const options = [...form.opcUaAvailableLocales];
  if (form.opcUaLocale && !options.some((l) => l.toLowerCase() === form.opcUaLocale.toLowerCase())) {
    options.unshift(form.opcUaLocale);
  }
  return options;
});
const localeHint = computed(() =>
  form.opcUaAvailableLocales.length > 0
    ? `Language of the display names and alarm texts. Locales reported by the server: ${form.opcUaAvailableLocales.length}.`
    : "Language of the display names and alarm texts. Run the connection test to read the locales of the server."
);
const passwordPlaceholder = computed(() =>
  !isNew.value && form.hasPassword ? "Leave empty to keep the current password" : ""
);

function applyDetail(detail: DataSourceDetail) {
  original.value = detail;
  form.name = detail.name;
  form.type = detail.type;
  form.isActive = detail.isActive;
  form.isSys = detail.isSys;
  form.host = detail.host;
  form.port = detail.port || defaultPort(detail.type);
  form.user = detail.user;
  form.signIn = !!detail.user;
  form.password = "";
  form.minCycle = null;
  form.testTagCount = detail.testTagCount || 10;
  form.testCycleMs = testCycles.includes(detail.testCycleMs) ? detail.testCycleMs : 1000;
  form.opcUaAlarmsEnabled = detail.opcUaAlarmsEnabled ?? true;
  form.opcUaLocale = detail.opcUaLocale ?? "";
  form.opcUaAvailableLocales = [...(detail.opcUaAvailableLocales ?? [])];
  form.s7AlarmsEnabled = detail.s7AlarmsEnabled ?? false;
  form.s7Locale = detail.s7Locale ?? "";
  form.clockWarnMs = detail.clockWarnMs || 10000;
  form.clockMonitor = detail.clockMonitor ?? true;
  form.clockCompensate = detail.clockCompensate ?? true;
  form.opcUaSecurityMode = (detail.opcUaSecurityMode || "none") as OpcUaSecurityMode;
  form.opcUaSecurityPolicy = detail.opcUaSecurityPolicy || "best";
  form.opcUaCertTrust = (detail.opcUaCertTrust || "any") as OpcUaCertTrust;
  form.opcUaServerCertThumbprint = detail.opcUaServerCertThumbprint ?? "";
  form.opcUaServerCertSubject = detail.opcUaServerCertSubject ?? "";
  form.opcUaServerCertNotAfterUtc = detail.opcUaServerCertNotAfterUtc ?? null;
  // A stored value outside the list stays selectable.
  if (!clockWarnOptions.some((o) => o.value === form.clockWarnMs)) {
    clockWarnOptions.push({ value: form.clockWarnMs, label: `${form.clockWarnMs / 1000} s` });
    clockWarnOptions.sort((a, b) => a.value - b.value);
  }
  form.hasPassword = detail.hasPassword;
}

function resetForm() {
  if (isNew.value) {
    form.name = "";
    form.type = "OpcUa";
    form.isActive = true;
    form.isSys = false;
    form.host = "";
    form.port = 4840;
    form.signIn = false;
    form.user = "";
    form.password = "";
    form.minCycle = null;
    form.testTagCount = 10;
    form.testCycleMs = 1000;
    form.opcUaAlarmsEnabled = true;
    form.opcUaLocale = "";
    form.opcUaAvailableLocales = [];
    form.s7AlarmsEnabled = false;
    form.s7Locale = "";
    form.clockWarnMs = 10000;
    form.clockMonitor = true;
    form.clockCompensate = true;
    form.opcUaSecurityMode = "signandencrypt";
    form.opcUaSecurityPolicy = "best";
    form.opcUaCertTrust = "pinned";
    form.opcUaServerCertThumbprint = "";
    form.opcUaServerCertSubject = "";
    form.opcUaServerCertNotAfterUtc = null;
    form.hasPassword = false;
    return;
  }
  if (original.value) {
    applyDetail(original.value);
  }
}

function cancelCurrent() {
  controller?.abort();
  busy.value = false;
  overlayError.value = "The request was cancelled.";
}

function toWrite(): DataSourceWrite {
  const isOpc = form.type === "OpcUa";
  const isTest = form.type === "Test";
  const hasConnection = isOpc || form.type === "S7CommPlus";
  return {
    name: form.name,
    type: form.type,
    isActive: form.isActive,
    host: hasConnection ? form.host : "",
    port: isOpc ? Number(form.port) || 4840 : hasConnection ? 102 : 0,
    user: isOpc && form.signIn ? form.user : "",
    password: isOpc && form.signIn ? form.password : "",
    minCycle: isOpc ? form.minCycle : null,
    testTagCount: isTest ? Number(form.testTagCount) || 10 : 0,
    testCycleMs: isTest ? Number(form.testCycleMs) || 1000 : 0,
    opcUaAlarmsEnabled: isOpc ? form.opcUaAlarmsEnabled : true,
    opcUaLocale: isOpc ? form.opcUaLocale : "",
    opcUaAvailableLocales: isOpc ? form.opcUaAvailableLocales : [],
    s7AlarmsEnabled: form.type === "S7CommPlus" ? form.s7AlarmsEnabled : false,
    s7Locale: form.type === "S7CommPlus" ? form.s7Locale : "",
    clockWarnMs: hasConnection ? Number(form.clockWarnMs) : null,
    clockMonitor: hasConnection ? form.clockMonitor : null,
    clockCompensate: hasConnection ? form.clockCompensate : null,
    opcUaSecurityMode: isOpc ? form.opcUaSecurityMode : null,
    opcUaSecurityPolicy: isOpc ? form.opcUaSecurityPolicy : null,
    opcUaCertTrust: isOpc ? form.opcUaCertTrust : null,
    opcUaServerCertThumbprint: isOpc ? form.opcUaServerCertThumbprint : null,
    opcUaServerCertSubject: isOpc ? form.opcUaServerCertSubject : null,
    opcUaServerCertNotAfterUtc: isOpc ? form.opcUaServerCertNotAfterUtc : null
  };
}

// A question of the connection test, and what goes on after the answer: the test again, or the save that needed it.
type DecisionKind = OpcUaCertificateDecision | "client";
const decision = ref<{ kind: DecisionKind; result: OpcUaConnectionTestResult; then: "test" | "save" } | null>(null);

// Check asks for the certificate also for a source that accepted any; the form changes only with Trust.
function checkCertificate() {
  void runConnectionTest("test", "pinned");
}

function cancelDecision() {
  decision.value = null;
}

function continueAfter(then: "test" | "save") {
  decision.value = null;
  void (then === "save" ? submit() : runConnectionTest());
}

function trustCertificate() {
  const certificate = decision.value?.result.serverCertificate;
  if (!decision.value || !certificate) return;
  form.opcUaServerCertThumbprint = certificate.thumbprint.toUpperCase();
  form.opcUaServerCertSubject = certificate.subject;
  form.opcUaServerCertNotAfterUtc = certificate.notAfterUtc;
  form.opcUaCertTrust = "pinned";
  continueAfter(decision.value.then);
}

function connectUnsecured() {
  if (!decision.value) return;
  form.opcUaSecurityMode = "none";
  continueAfter(decision.value.then);
}

function testAgain() {
  if (!decision.value) return;
  continueAfter(decision.value.then);
}

async function runConnectionTest(then: "test" | "save" = "test", trust: OpcUaCertTrust = form.opcUaCertTrust) {
  if (form.isSys || leaving.value) {
    return;
  }

  if (!form.host.trim()) {
    error.value = "Please enter a valid IP address.";
    activeTab.value = "connection";
    await nextTick();
    hostInput.value?.focus();
    return;
  }

  controller?.abort();
  controller = new AbortController();
  const port = isS7.value ? 102 : Number(form.port) || 4840;
  busy.value = true;
  overlayError.value = "";
  overlayTitle.value = "Connection test";
  overlayMessage.value = isS7.value
    ? `Connecting to the PLC ${form.host.trim()}:${port} …`
    : `Connecting to opc.tcp://${form.host.trim()}:${port} …`;
  error.value = "";

  if (isS7.value) {
    try {
      const result = await testS7CommPlusConnection(
        {
          dataSourceId: isNew.value ? null : sourceId.value,
          host: form.host,
          port,
          user: "",
          password: ""
        },
        controller.signal
      );
      if (!result.success) {
        error.value = `Connection test failed: ${result.error}`;
        overlayError.value = error.value;
        overlayTitle.value = "Error";
        return;
      }
      busy.value = false;
      s7Result.value = result;
    } catch (err) {
      if (isAbortError(err)) {
        return;
      }
      error.value = err instanceof Error ? err.message : "Connection test failed.";
      overlayError.value = error.value;
      overlayTitle.value = "Error";
    }
    return;
  }

  try {
    const result = await testOpcUaConnection(
      {
        dataSourceId: isNew.value ? null : sourceId.value,
        host: form.host,
        port,
        user: form.signIn ? form.user : "",
        password: form.signIn ? form.password : "",
        opcUaSecurityMode: form.opcUaSecurityMode,
        opcUaSecurityPolicy: form.opcUaSecurityPolicy,
        opcUaCertTrust: trust,
        opcUaServerCertThumbprint: form.opcUaServerCertThumbprint
      },
      controller.signal
    );
    // The server certificate is never trusted silently: the test stops before the sign-in and asks.
    if (result.certificateDecision || (!result.success && result.clientNotTrusted)) {
      busy.value = false;
      decision.value = { kind: result.certificateDecision || "client", result, then };
      return;
    }
    // The server wants a user: the sign-in is turned on and the user name is next to fill in.
    if (result.signInRequired) {
      busy.value = false;
      error.value = "The server does not allow an anonymous session: please enter the user name and password.";
      form.signIn = true;
      activeTab.value = "connection";
      await nextTick();
      userInput.value?.focus();
      return;
    }
    if (!result.success) {
      error.value = `Connection test failed: ${result.error}`;
      if (result.serverCertificate || result.endpoints.length) {
        // The dialog shows the endpoints and the certificate, which helps to fix the security.
        busy.value = false;
        opcResult.value = result;
        return;
      }
      overlayError.value = error.value;
      overlayTitle.value = "Error";
      return;
    }

    // A check of a source that accepted any certificate passed at the pin it still had: it trusts that one now.
    if (trust === "pinned" && form.opcUaCertTrust === "any" && result.serverCertificate) {
      form.opcUaCertTrust = "pinned";
      form.opcUaServerCertThumbprint = result.serverCertificate.thumbprint.toUpperCase();
      form.opcUaServerCertSubject = result.serverCertificate.subject;
      form.opcUaServerCertNotAfterUtc = result.serverCertificate.notAfterUtc;
    }
    form.opcUaAvailableLocales = result.locales;
    if (result.minSampleRate != null) {
      form.minCycle = result.minSampleRate;
    }
    // A server without a locale list keeps the choice; otherwise it must be one of the reported locales.
    if (result.locales.length > 0) {
      const kept = result.locales.find((l) => l.toLowerCase() === form.opcUaLocale.toLowerCase());
      form.opcUaLocale = kept ?? "";
    }
    busy.value = false;
    opcResult.value = result;
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    error.value = err instanceof Error ? err.message : "Connection test failed.";
    overlayError.value = error.value;
    overlayTitle.value = "Error";
  }
}

async function loadOtherSources() {
  try {
    otherSources.value = await getDataSources();
  } catch {
    // Only for the duplicate hint.
    otherSources.value = [];
  }
}

async function loadDetail() {
  leaving.value = false;
  activeTab.value = "connection";
  void loadOtherSources();
  if (isNew.value) {
    resetForm();
    return;
  }

  controller?.abort();
  controller = new AbortController();
  busy.value = true;
  overlayError.value = "";
  overlayTitle.value = "Loading";
  overlayMessage.value = "Loading data source …";
  error.value = "";

  try {
    const detail = await getDataSource(sourceId.value, controller.signal);
    applyDetail(detail);
    busy.value = false;
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    error.value = err instanceof Error ? err.message : "Load failed.";
    overlayError.value = error.value;
    overlayTitle.value = "Error";
    busy.value = true;
  }
}

// The question is a dialog of the page; the confirm of the browser is not shown everywhere
const deleteOpen = ref(false);

async function confirmDelete() {
  deleteOpen.value = false;
  if (isNew.value || form.isSys || leaving.value) {
    return;
  }

  controller?.abort();
  controller = new AbortController();
  busy.value = true;
  overlayError.value = "";
  overlayTitle.value = "Delete";
  overlayMessage.value = `Deleting "${form.name}" …`;
  error.value = "";

  try {
    await deleteDataSource(sourceId.value, controller.signal);
    busy.value = false;
    leaving.value = true;
    notify("success", "Success", "The data source was deleted. Its data tags are removed in the background.");
    if (!closed) {
      await router.replace("/datasources");
    }
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    error.value = err instanceof Error ? err.message : "Delete failed.";
    overlayError.value = error.value;
    overlayTitle.value = "Error";
  }
}

function wait(ms: number) {
  return new Promise<void>((resolve) => {
    redirectTimer = setTimeout(resolve, ms);
  });
}

async function submit() {
  if (form.isSys || leaving.value) {
    return;
  }

  if (!form.name.trim()) {
    error.value = "Name is required.";
    return;
  }

  if (showOpcFields.value && form.signIn && !form.user.trim()) {
    error.value = "Please enter a username or turn off the sign-in.";
    activeTab.value = "connection";
    await nextTick();
    userInput.value?.focus();
    return;
  }

  // Without a trusted certificate the save asks for it first, with the same question as the connection test.
  if (
    showOpcFields.value &&
    form.opcUaSecurityMode !== "none" &&
    form.opcUaCertTrust === "pinned" &&
    !form.opcUaServerCertThumbprint
  ) {
    activeTab.value = "connection";
    await runConnectionTest("save");
    return;
  }

  controller?.abort();
  controller = new AbortController();
  busy.value = true;
  overlayError.value = "";
  overlayTitle.value = isNew.value ? "Create" : "Save";
  overlayMessage.value = "Saving changes …";
  error.value = "";

  try {
    if (isNew.value) {
      await createDataSource(toWrite(), controller.signal);
      busy.value = false;
      leaving.value = true;
      notify("success", "Success", "The data source was created.");
      await wait(1600);
      if (!closed) {
        await router.replace("/datasources");
      }
      return;
    }

    await updateDataSource(sourceId.value, toWrite(), controller.signal);
    busy.value = false;
    leaving.value = true;
    notify("success", "Success", "The data source was saved.");
    await wait(1600);
    if (!closed) {
      await router.replace("/datasources");
    }
  } catch (err) {
    if (isAbortError(err)) {
      return;
    }
    error.value = err instanceof Error ? err.message : "Save failed.";
    overlayError.value = error.value;
    overlayTitle.value = "Error";
    busy.value = true;
  }
}

watch(
  () => [route.name, route.params.id],
  () => {
    void loadDetail();
  },
  { immediate: true }
);

onUnmounted(() => {
  closed = true;
  controller?.abort();
  if (redirectTimer) {
    clearTimeout(redirectTimer);
  }
});
</script>

<style scoped>
.source-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem;
  border-bottom: 1px solid var(--border);
}

.source-tab {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  margin-bottom: -1px;
  padding: 0.35rem 0.8rem;
  border: 1px solid transparent;
  border-bottom: 0;
  border-radius: var(--radius) var(--radius) 0 0;
  background: transparent;
  color: var(--muted);
  font: inherit;
  font-weight: 600;
  cursor: pointer;
}

.source-tab:hover {
  color: var(--text);
}

.source-tab.active {
  border-color: var(--border);
  background: var(--surface);
  color: var(--text);
  padding-bottom: calc(0.35rem + 1px);
}

.source-tab-dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 50%;
  background: var(--warn);
}

.source-tab-panel {
  display: grid;
  gap: 0.85rem;
}

.source-subhead {
  margin: 0.2rem 0 0;
  font-size: var(--font-ui);
  font-weight: 600;
  color: var(--muted);
}

/* A field that only counts with the check box above it */
.source-dependent {
  /* Starts with the title of the check box: its padding, border, box and gap */
  padding-left: calc(0.75rem + 1px + 13px + 0.65rem);
}

.source-field-hint.source-check-warning {
  color: var(--warn);
}

/* Like .field label, for the status that is no form control */
.field-label {
  font-size: var(--font-ui);
  color: var(--muted);
}

/* Looks like the fields next to it, so the row stays even */
.cert-status {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  height: 2rem;
  /* A long name is cut with an ellipsis instead of widening the column */
  min-width: 0;
  padding: 0 0.3rem 0 0.7rem;
  box-sizing: border-box;
  border: 1px solid var(--border);
  border-radius: 3px;
  background: var(--surface-alt);
  font-size: var(--font-ui);
}

.cert-status-text {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cert-status.is-ok .cert-status-text { color: var(--ok); }
.cert-status.is-warn .cert-status-text { color: var(--warn); }
.cert-status.is-danger .cert-status-text { color: var(--danger); }
.cert-status.is-muted .cert-status-text { color: var(--muted); }

.cert-status-action {
  flex: 0 0 auto;
  padding: 0.15rem 0.5rem;
  border: 0;
  border-radius: 3px;
  background: transparent;
  color: var(--accent);
  font: inherit;
  font-weight: 600;
  cursor: pointer;
}

.cert-status-action:hover:not(:disabled) {
  background: var(--row-selected);
}

/* An icon only, so the name and the date keep their room */
.cert-status-remove {
  width: 1.5rem;
  height: 1.5rem;
  padding: 0;
  color: var(--muted);
  font-size: 1.1rem;
  font-weight: 400;
  line-height: 1;
}

.cert-status-remove:hover:not(:disabled) {
  color: var(--danger);
}

.source-check-inline {
  align-self: end;
  align-items: center;
  height: 2rem;
  box-sizing: border-box;
  padding-top: 0;
  padding-bottom: 0;
}

.source-check-inline input {
  margin-top: 0;
}

.decision-card {
  width: min(520px, calc(100vw - 2rem));
}

.decision-text {
  margin: 0;
  line-height: 1.4;
}

.decision-warning {
  color: var(--warn);
}

.source-security-note {
  margin: 0;
  color: var(--muted);
  font-size: var(--font-ui);
  line-height: 1.35;
  overflow-wrap: anywhere;
}

.source-security-note.source-check-warning {
  color: var(--warn);
}

.cert-thumbprint {
  font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
  font-size: 0.8rem;
  overflow-wrap: anywhere;
}

.opc-result-error {
  margin: 0.4rem 0 0.6rem;
  overflow-wrap: anywhere;
}

.s7-result-overlay {
  position: fixed;
  z-index: 20;
}

.s7-result-card {
  width: min(460px, calc(100vw - 2rem));
}

.s7-result-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9rem;
}

.s7-result-table th {
  text-align: left;
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--muted, #6b7785);
  padding: 0.7rem 0 0.25rem;
}

.s7-result-table tbody:first-child tr:first-child th {
  padding-top: 0;
}

.s7-result-table td {
  padding: 0.2rem 0;
  white-space: normal;
  overflow-wrap: anywhere;
  border-bottom: 1px solid var(--border);
}

.s7-result-table td:first-child {
  color: var(--muted, #6b7785);
  padding-right: 1rem;
  /* Labels stay whole; long values (thumbprints, URLs) break instead */
  white-space: nowrap;
  overflow-wrap: normal;
  width: 1%;
}

.s7-result-table .num {
  text-align: right;
  font-variant-numeric: tabular-nums;
  padding-left: 0.75rem;
}

.opc-result-card {
  width: min(1070px, calc(100vw - 2rem));
  max-height: calc(100vh - 2rem);
  overflow-y: auto;
}

.opc-result-columns {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.7rem 1.5rem;
  align-items: start;
}

@media (max-width: 720px) {
  .opc-result-columns {
    grid-template-columns: 1fr;
    gap: 0;
  }
}

.s7-result-table td.clock-off {
  color: var(--warn);
  font-weight: 600;
}

.s7-result-actions {
  display: flex;
  justify-content: flex-end;
}
</style>
