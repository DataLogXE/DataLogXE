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

using DataLogLib.Models;
using DataLogLib.OpcUaNodeset;
using DataLogLib.Services.EventLog;
using DataLogService.DataSource.OpcUa;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System.Security.Cryptography.X509Certificates;

namespace OpcUaSimpleClientApi
{
    #region enums
    public enum ConnectionState
    {
        Connecting,
        Connected,
        Error,
        Disconnected,
        Disconnecting
    };
    #endregion

    public class OpcUaClientBase
    {
        #region properties
        public ConnectionState State { get; private set; }
        public long AutoReconnectMillis { get; set; } = 30000;
        public long KeepAliveTimeoutMillis { get; set; } = 20000;
        public bool CanConnect { get; private set; }
        public bool CanDisconnect { get; private set; }
        public bool IsConnected
        {
            get { return State == ConnectionState.Connected; }
        }

        /// <summary>
        /// Max allowed nodes to register / unregister monitored item nodes
        /// in one register cycle
        /// </summary>
        public int MaxNodesPerRegisterNodes { get; private set; } = 100;

        /// <summary>
        /// Max nodes per Browse service call
        /// </summary>
        public int MaxNodesPerBrowse { get; private set; } = 100;

        /// <summary>
        /// Max nodes per Read service call
        /// </summary>
        public int MaxNodesPerRead { get; private set; } = 1000;

        /// <summary>
        /// Server.ServerCapabilities.MinSupportedSampleRate in ms (rounded up), null when the server does not report it
        /// </summary>
        public int? MinSupportedSampleRate { get; private set; }

        /// <summary>
        /// Why the last connect failed when it was the security (a rejected server certificate, a server that does
        /// not trust DataLogXE), null otherwise; shown in the connection alarm.
        /// </summary>
        public string LastSecurityError { get; private set; }

        /// <summary>
        /// The server certificate the last connect refused because it is not the pinned one, null otherwise; the data
        /// sources list shows it as a changed certificate.
        /// </summary>
        public string LastRejectedThumbprint { get; private set; }
        #endregion

        #region member
        protected string _appName;
        private string _opcUrl;
        private string _username;
        private string _password;
        private string _locale;
        private OpcUaSecuritySettings _security = OpcUaSecuritySettings.Unsecured;
        private OpcUaCertificateGuard _certGuard = new(OpcUaSecuritySettings.Unsecured);
        private EndpointDescription _secureEndpoint;

        private ApplicationConfiguration _clientAppConfig;
        private ApplicationConfiguration _browseAppConfig;
        private DateTime _keepAlive;
        private int _auxBrowseSessionCount;
        protected Session _serverSession;

        protected List<string> _namespaceArray = new List<string>();
        #endregion

        #region construction
        public OpcUaClientBase()
        {
            SetState(ConnectionState.Disconnected);
        }
        #endregion

        #region connect / disconnect
        public void Connect(string opcUrl, string appName = "OpcUaClientApi", string username = "", string password = "",
            string locale = "", OpcUaSecuritySettings security = null)
        {
            if (State == ConnectionState.Disconnected)
            {
                _opcUrl = opcUrl;
                _appName = appName;
                _username = username;
                _password = password;
                _locale = locale;
                _security = security ?? OpcUaSecuritySettings.Unsecured;
                _certGuard = new OpcUaCertificateGuard(_security);
                _secureEndpoint = null;
                _clientAppConfig = null;
                _browseAppConfig = null;

                if (CheckAddressValid())
                    StartStateMachineThread();
            }
            else
            {
                DoOnClientError("Can not connect -> disconnect first");
            }
        }

        public void Disconnect()
        {
            if (State == ConnectionState.Error || State == ConnectionState.Connecting || State == ConnectionState.Connected)
                SetState(ConnectionState.Disconnecting);
        }

        /// <summary>
        /// Drops a connected session as an error (like a keep alive timeout); the state machine closes it and
        /// connects again after the reconnect delay.
        /// </summary>
        public void DropConnection(string reason)
        {
            if (State == ConnectionState.Connected)
            {
                DoOnClientError(reason);
                SetState(ConnectionState.Error);
            }
        }
        #endregion

        #region connection state machine
        private void StartStateMachineThread()
        {
            var stateMachineThrad = new Thread(StateMachineThread)
            {
                Name = $"{_appName}OpcUaThread",
                IsBackground = true
            };
            stateMachineThrad.Start();
        }

        private void StateMachineThread()
        {
            SetState(ConnectionState.Connecting);

            while (State == ConnectionState.Connecting)
            {
                try
                {
                    using (_serverSession = CreateClientSession())
                    {
                        // client is connected ...
                        if (State == ConnectionState.Connecting)
                        {
                            LastSecurityError = null;
                            LastRejectedThumbprint = null;
                            UpdateNamespaceArray();
                            UpdateServerCapabilities();
                            SetState(ConnectionState.Connected);
                        }

                        // add keep alive event
                        _serverSession.KeepAlive -= ClientSession_KeepAlive;
                        _serverSession.KeepAlive += ClientSession_KeepAlive;

                        // once connected -> check keep alive or disconnect
                        WaitAndCheckKeepAlive();

                        // Close server session
                        _serverSession.Close();
                    }
                }
                catch (Exception ex)
                {
                    var text = _certGuard.Describe(ex, !string.IsNullOrEmpty(_username));
                    // Set before the state, so the connection alarm already carries the reason.
                    LastSecurityError = _certGuard.IsSecurityFailure(ex) ? text : null;
                    var rejected = _certGuard.LastRejectedThumbprint;
                    LastRejectedThumbprint = rejected.Length > 0 ? rejected : null;
                    SetState(ConnectionState.Error);
                    DoOnClientError(text);
                }

                WaitUntilReconnect();
            }

            SetState(ConnectionState.Disconnected);
        }

        private void WaitAndCheckKeepAlive()
        {
            _keepAlive = DateTime.Now;
            var lastProbe = DateTime.MinValue;

            while (State == ConnectionState.Connected)
            {
                var diff = GetMillisDiff(_keepAlive);
                if (diff > KeepAliveTimeoutMillis && Volatile.Read(ref _auxBrowseSessionCount) == 0)
                {
                    DoOnClientError("Keep alive timeout");
                    SetState(ConnectionState.Error);
                    break;
                }

                // Prefer the session KeepAlive callback; only probe when no browse
                // sessions are open and at most every 10 seconds.
                if (Volatile.Read(ref _auxBrowseSessionCount) == 0
                    && GetMillisDiff(lastProbe) >= 10000)
                {
                    try
                    {
                        var probe = _serverSession.ReadValue(Variables.Server_ServerStatus_CurrentTime);
                        lastProbe = DateTime.Now;
                        // A clock sample with a known short delay: the response came within the round trip.
                        if (probe?.Value is DateTime serverTime && serverTime != DateTime.MinValue)
                        {
                            OnServerKeepAlive?.Invoke(this, new ServerKeepAliveEventArgs(serverTime));
                        }
                    }
                    catch (Exception ex)
                    {
                        if (GetMillisDiff(_keepAlive) > KeepAliveTimeoutMillis)
                        {
                            DoOnClientError(ex.Message);
                            SetState(ConnectionState.Error);
                            break;
                        }
                    }
                }

                Thread.Sleep(1000);
            }
        }

        private void WaitUntilReconnect()
        {
            var reconnectMillis = AutoReconnectMillis;

            // log reconnect delay
            if (State == ConnectionState.Error)
            {
                DoOnClientMessage($"Try to reconnect in {reconnectMillis / 1000}s");
            }

            var waitStart = DateTime.Now;

            while (State == ConnectionState.Error)
            {
                var diffMs = GetMillisDiff(waitStart);

                if (diffMs > reconnectMillis)
                    SetState(ConnectionState.Connecting);
                else
                    Thread.Sleep(100);
            }
        }

        protected void SetState(ConnectionState newState)
        {
            if (State != newState)
            {
                State = newState;
                UpdateCanConnect();
                DoOnStateChange(newState);
            }
        }

        private void ClientSession_KeepAlive(ISession session, KeepAliveEventArgs e)
        {
            if (e.CurrentState == ServerState.Running)
            {
                // store keep alive time to check timeout
                _keepAlive = DateTime.Now;
            }

            // inform about the session keep alive event 
            DoOnServerKeepAlive(e);
        }

        private void UpdateCanConnect()
        {
            CanConnect = State == ConnectionState.Disconnected;

            CanDisconnect =
                State == ConnectionState.Connected ||
                State == ConnectionState.Error ||
                State == ConnectionState.Connecting;
        }
        #endregion

        #region common helper
        private long GetMillisDiff(DateTime last)
        {
            var now = DateTime.Now;
            TimeSpan diff = now - last;
            return (long)diff.TotalMilliseconds;
        }

        private bool CheckAddressValid()
        {
            string s = "opc.tcp://";
            if (_opcUrl.ToLower().StartsWith(s))
                return true;
            else
                DoOnClientError($"Address does not start with '{s}'");
            return false;
        }
        #endregion

        #region opc helper
        private ApplicationConfiguration CreateClientConfiguration(int operationTimeoutMillis = 15000, string applicationName = null)
        {
            return CreateClientConfiguration(_appName, operationTimeoutMillis, applicationName, _certGuard);
        }

        /// <summary>
        /// The months a new client certificate is valid; a server that trusts it then keeps working for 5 years.
        /// </summary>
        private const ushort ClientCertificateLifetimeMonths = 60;

        /// <summary>
        /// The client configuration; <paramref name="certAppName"/> names the application certificate and the
        /// application URI, so all sessions of one application present the same certificate to the server.
        /// </summary>
        private static ApplicationConfiguration CreateClientConfiguration(string certAppName, int operationTimeoutMillis,
            string applicationName, OpcUaCertificateGuard certGuard)
        {
            certGuard ??= new OpcUaCertificateGuard(OpcUaSecuritySettings.Unsecured);
            var appName = string.IsNullOrWhiteSpace(applicationName) ? certAppName : applicationName;
            var certRoot = Environment.GetEnvironmentVariable("OPCUA_CERT_PATH");
            if (string.IsNullOrWhiteSpace(certRoot))
            {
                certRoot = "/app/opcua";
            }
            var config = new ApplicationConfiguration()
            {
                ApplicationName = appName,
                ApplicationUri = Utils.Format(@"urn:{0}:{1}", System.Net.Dns.GetHostName(), certAppName),
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier { StoreType = @"Directory", StorePath = certRoot + "/own", SubjectName = certAppName },
                    TrustedIssuerCertificates = new CertificateTrustList { StoreType = @"Directory", StorePath = certRoot + "/issuers" },
                    TrustedPeerCertificates = new CertificateTrustList { StoreType = @"Directory", StorePath = certRoot + "/trusted" },
                    RejectedCertificateStore = new CertificateTrustList { StoreType = @"Directory", StorePath = certRoot + "/rejected" },
                    AutoAcceptUntrustedCertificates = certGuard.AcceptsAny
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = operationTimeoutMillis,
                    MaxStringLength = 64 * 1024,
                    MaxByteStringLength = 64 * 1024,
                    MaxArrayLength = 65535,
                    MaxMessageSize = 512 * 1024,
                    MaxBufferSize = 64 * 1024,
                    ChannelLifetime = 4096
                },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
                TraceConfiguration = new TraceConfiguration()
            };

            // validate configuration
            config.Validate(ApplicationType.Client).GetAwaiter().GetResult();

            // The certificate is kept in a volume while the host name (part of the configured URI) changes with every
            // new container; the stack refuses a stored certificate whose URI differs from the configured one, so the
            // configuration takes the URI of the stored certificate before the check, and a secured session presents it.
            var storedUri = StoredCertificateUri(certRoot + "/own/certs", certAppName);
            if (!string.IsNullOrEmpty(storedUri))
            {
                config.ApplicationUri = storedUri;
            }

            // Unsecured sessions and "any" accept every server certificate, "pinned" only the pinned one
            config.CertificateValidator.CertificateValidation += certGuard.OnValidation;

            // create client application
            var application = new ApplicationInstance
            {
                ApplicationName = appName,
                ApplicationType = ApplicationType.Client,
                ApplicationConfiguration = config
            };
            application.CheckApplicationInstanceCertificates(false, ClientCertificateLifetimeMonths, CancellationToken.None)
                .GetAwaiter().GetResult();

            // A certificate created by the check carries the configured URI
            var certificate = config.SecurityConfiguration.ApplicationCertificate.Certificate;
            if (certificate != null)
            {
                var certificateUri = X509Utils.GetApplicationUriFromCertificate(certificate);
                if (!string.IsNullOrEmpty(certificateUri))
                {
                    config.ApplicationUri = certificateUri;
                }
            }

            return config;
        }

        /// <summary>
        /// The application URI of the newest stored certificate of <paramref name="certAppName"/>, or null.
        /// </summary>
        private static string StoredCertificateUri(string certsPath, string certAppName)
        {
            try
            {
                if (!Directory.Exists(certsPath)) return null;
                return Directory.GetFiles(certsPath, certAppName + " [*].der")
                    .Select(f => System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(f))
                    .Where(c => c.Subject == "CN=" + certAppName)
                    .OrderByDescending(c => c.NotAfter)
                    .Select(c => X509Utils.GetApplicationUriFromCertificate(c))
                    .FirstOrDefault(u => !string.IsNullOrEmpty(u));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private ConfiguredEndpoint CreateEndpoint(ApplicationConfiguration config)
        {
            if (_security.IsUnsecured)
            {
                EndpointDescriptionCollection offered = null;
                try
                {
                    offered = DiscoverEndpoints(_opcUrl, config);
                }
                catch (Exception)
                {
                    // Without the list the session tries the unsecured endpoint as before.
                }

                var endpoint = CreateUnsecuredEndpoint(_opcUrl, config, offered);
                if (endpoint.Description.SecurityMode != MessageSecurityMode.None)
                {
                    DoOnClientMessage("The server offers no endpoint without security, the session uses " +
                        $"{endpoint.Description.SecurityMode} {OpcUaEndpointSelector.PolicyName(endpoint.Description.SecurityPolicyUri)}");
                }
                return endpoint;
            }

            // The main session discovers the endpoint; the browse sessions take the same one.
            var description = _secureEndpoint;
            if (description == null)
            {
                description = OpcUaEndpointSelector.Select(DiscoverEndpoints(_opcUrl, config), _security, _opcUrl);
                _secureEndpoint = description;
            }

            return new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));
        }

        /// <summary>
        /// The endpoint up to 0.0.199: no security, the session updates it from the server before it connects. A server
        /// that offers no endpoint without security gets the best secured one (see
        /// <see cref="OpcUaEndpointSelector.SelectInsteadOfUnsecured"/>).
        /// </summary>
        private static ConfiguredEndpoint CreateUnsecuredEndpoint(string opcUrl, ApplicationConfiguration config,
            EndpointDescriptionCollection offered)
        {
            var secured = OpcUaEndpointSelector.SelectInsteadOfUnsecured(offered, opcUrl);
            if (secured != null)
            {
                return new ConfiguredEndpoint(null, secured, EndpointConfiguration.Create(config));
            }

            EndpointDescription endpointDescription = new EndpointDescription
            {
                EndpointUrl = opcUrl,
                SecurityPolicyUri = SecurityPolicies.None,
                SecurityMode = MessageSecurityMode.None,
                TransportProfileUri = Profiles.UaTcpTransport
            };

            EndpointConfiguration endpointConfiguration = EndpointConfiguration.Create(config);
            return new ConfiguredEndpoint(null, endpointDescription, endpointConfiguration);
        }

        /// <summary>
        /// The endpoints the server offers (GetEndpoints on an unsecured discovery channel).
        /// </summary>
        private static EndpointDescriptionCollection DiscoverEndpoints(string opcUrl, ApplicationConfiguration config)
        {
            var endpointConfiguration = EndpointConfiguration.Create(config);
            using var client = DiscoveryClient.Create(config, new Uri(opcUrl), endpointConfiguration);
            return client.GetEndpoints(null);
        }

        private UserIdentity CreateUserIdentity()
        {
            return CreateUserIdentity(_username, _password);
        }

        private static UserIdentity CreateUserIdentity(string username, string password)
        {
            if (username == null || username == "")
                return null;
            else
                return new UserIdentity(username, System.Text.Encoding.UTF8.GetBytes(password ?? ""));
        }

        /// <summary>
        /// The preferred locales of a session: null leaves the choice to the server.
        /// </summary>
        private static string[] PreferredLocales(string locale)
        {
            return string.IsNullOrWhiteSpace(locale) ? null : [locale.Trim()];
        }

        private Session CreateClientSession()
        {
            // create application configuration object
            _clientAppConfig = CreateClientConfiguration();

            // create endpoint definition; each connect discovers it again, since an endpoint kept from before still
            // carries the old server certificate after a renewal and the server would only close the channel
            _certGuard.ResetRejected();
            _secureEndpoint = null;
            ConfiguredEndpoint endpoint = CreateEndpoint(_clientAppConfig);
            UserIdentity userIdentity = CreateUserIdentity();

            // create opc session; an unsecured endpoint is updated from the server, a secured one was discovered
            var session = Session.Create(_clientAppConfig,
                     endpoint,
                     _security.IsUnsecured,
                     $"{_appName}Session",
                     60000,
                     userIdentity,
                     PreferredLocales(_locale));

            var s = session.GetAwaiter().GetResult();

            return s;
        }

        public List<OpcUaAuxSession> OpenBrowseSessions(int maxCount, CancellationToken cancellationToken)
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(_opcUrl))
            {
                throw new InvalidOperationException("The OPC UA session is not connected.");
            }

            maxCount = Math.Clamp(maxCount, 1, 2);
            var sessions = new List<OpcUaAuxSession>(maxCount);
            try
            {
                for (var i = 1; i <= maxCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i > 1)
                    {
                        if (cancellationToken.WaitHandle.WaitOne(200))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    try
                    {
                        sessions.Add(CreateAuxSession(i.ToString()));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        DoOnClientError($"Browse session {i} could not be opened: {ex.Message}");
                        break;
                    }
                }

                if (sessions.Count == 0)
                {
                    throw new InvalidOperationException("A browse session could not be opened.");
                }

                Interlocked.Add(ref _auxBrowseSessionCount, sessions.Count);
                return sessions;
            }
            catch
            {
                DisposeBrowseSessions(sessions);
                throw;
            }
        }

        public void CloseBrowseSessions(IReadOnlyList<OpcUaAuxSession> sessions)
        {
            var count = DisposeBrowseSessions(sessions);
            if (count > 0)
            {
                Interlocked.Add(ref _auxBrowseSessionCount, -count);
            }
        }

        private static int DisposeBrowseSessions(IReadOnlyList<OpcUaAuxSession> sessions)
        {
            if (sessions == null)
            {
                return 0;
            }

            var count = 0;
            foreach (var session in sessions)
            {
                if (session == null)
                {
                    continue;
                }

                count++;
                session.Dispose();
            }

            return count;
        }

        private OpcUaAuxSession CreateAuxSession(string suffix)
        {
            var appName = $"{_appName}Browse{suffix}";
            _browseAppConfig ??= CreateClientConfiguration(30000, $"{_appName}Browse");
            var config = _browseAppConfig;
            var endpoint = CreateEndpoint(config);
            var userIdentity = CreateUserIdentity();
            var session = Session.Create(
                    config,
                    endpoint,
                    _security.IsUnsecured,
                    $"{appName}Session",
                    60000,
                    userIdentity,
                    PreferredLocales(_locale))
                .GetAwaiter()
                .GetResult();

            try
            {
                var namespaces = new List<string>();
                for (uint i = 0; i < session.NamespaceUris.Count; i++)
                {
                    namespaces.Add(session.NamespaceUris.GetString(i));
                }

                var maxBrowse = TryReadOperationLimit(
                    session, Variables.Server_ServerCapabilities_OperationLimits_MaxNodesPerBrowse, 100);
                var maxRead = TryReadOperationLimit(
                    session, Variables.Server_ServerCapabilities_OperationLimits_MaxNodesPerRead, 1000);

                return new OpcUaAuxSession(session, namespaces, maxBrowse, maxRead);
            }
            catch
            {
                try
                {
                    session.Close();
                }
                catch
                {
                    // Closing a failed aux session is best-effort.
                }

                session.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Opens a session with the given address and user, reads the product name and the locales of the server and
        /// closes it again; a failure is returned in the result, not thrown (except a cancellation).
        /// </summary>
        public static OpcUaConnectionTestResult TestConnection(string opcUrl, string appName, string username,
            string password, int timeoutMillis, CancellationToken cancellationToken,
            OpcUaSecuritySettings security = null)
        {
            security ??= OpcUaSecuritySettings.Unsecured;
            var result = new OpcUaConnectionTestResult();
            // The test checks the server certificate as the data source will: a pinned source signs in only at the
            // pinned certificate, so the password never reaches a server that is not trusted.
            var guard = new OpcUaCertificateGuard(security);
            Session session = null;
            try
            {
                if (!opcUrl.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Address does not start with 'opc.tcp://'");
                }

                var config = CreateClientConfiguration(appName, timeoutMillis, $"{appName}Test", guard);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(timeoutMillis);

                ConfiguredEndpoint endpoint;
                EndpointDescriptionCollection offered = null;
                try
                {
                    offered = DiscoverEndpoints(opcUrl, config);
                }
                catch (Exception) when (security.IsUnsecured)
                {
                    // The unsecured session updates its endpoint itself; the list is only extra information.
                }

                timeout.Token.ThrowIfCancellationRequested();
                var chosen = FillEndpointInfo(result, offered, security, opcUrl);
                if (NeedsDecision(result, offered, chosen, security))
                {
                    return result;
                }

                if (security.IsUnsecured)
                {
                    endpoint = CreateUnsecuredEndpoint(opcUrl, config, offered);
                }
                else
                {
                    var description = OpcUaEndpointSelector.Select(offered, security, opcUrl);
                    endpoint = new ConfiguredEndpoint(null, description, EndpointConfiguration.Create(config));
                }

                session = Session.Create(
                        config,
                        endpoint,
                        security.IsUnsecured,
                        $"{appName}TestSession",
                        (uint)timeoutMillis,
                        CreateUserIdentity(username, password),
                        null,
                        timeout.Token)
                    .GetAwaiter()
                    .GetResult();

                result.ProductName = TryReadValue(session, Variables.Server_ServerStatus_BuildInfo_ProductName) as string ?? "";
                if (TryReadValue(session, Variables.Server_ServerCapabilities_LocaleIdArray) is string[] locales)
                {
                    result.Locales = locales
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Select(l => l.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                result.MinSampleRate = ReadMinSupportedSampleRate(session);
                result.ClockOffsetMs = ReadClockOffsetMs(session);
                ReadConnectionDetails(session, username, result);
                result.Success = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result.Error = $"No answer from the server within {timeoutMillis / 1000} s.";
            }
            catch (Exception ex)
            {
                result.Error = guard.Describe(ex, !string.IsNullOrEmpty(username));
                result.ClientNotTrusted = guard.IsClientTrustFailure(ex);
                result.SignInRequired = string.IsNullOrEmpty(username) && OpcUaCertificateGuard.IsSignInFailure(ex);
            }
            finally
            {
                if (session != null)
                {
                    try
                    {
                        session.Close();
                    }
                    catch
                    {
                        // Closing the test session is best-effort.
                    }

                    session.Dispose();
                }
            }

            return result;
        }

        /// <summary>
        /// Stops the test before the session when the user has to decide first: a secured test against a server
        /// with unsecured endpoints only, or a pinned test whose server shows a certificate that is not pinned.
        /// Without a fitting endpoint the session attempt reports the offered endpoints instead.
        /// </summary>
        internal static bool NeedsDecision(OpcUaConnectionTestResult result, IReadOnlyCollection<EndpointDescription> offered,
            EndpointDescription chosen, OpcUaSecuritySettings security)
        {
            if (security.IsUnsecured || offered == null || offered.Count == 0)
            {
                return false;
            }

            if (offered.All(e => e.SecurityMode == MessageSecurityMode.None))
            {
                result.CertificateDecision = OpcUaCertificateDecision.UnsecuredOnly;
                result.Error = "The server offers unsecured endpoints only.";
                return true;
            }

            if (chosen == null || security.AcceptsAnyCertificate || result.ServerCertificate == null)
            {
                return false;
            }

            var shown = OpcUaSecurityValues.NormalizeThumbprint(result.ServerCertificate.Thumbprint);
            if (shown == security.PinnedThumbprint)
            {
                return false;
            }

            if (security.PinnedThumbprint.Length == 0)
            {
                result.CertificateDecision = OpcUaCertificateDecision.New;
                result.Error = "The server certificate is not trusted yet.";
            }
            else
            {
                result.CertificateDecision = OpcUaCertificateDecision.Changed;
                result.Error = $"The server shows another certificate than the trusted {security.PinnedThumbprint}.";
            }

            return true;
        }

        /// <summary>
        /// The endpoints of the server and the certificate of the endpoint the session will use (or of any endpoint
        /// for an unsecured session, so it can be shown). Returns the endpoint for the security, null when none fits.
        /// </summary>
        private static EndpointDescription FillEndpointInfo(OpcUaConnectionTestResult result,
            EndpointDescriptionCollection offered, OpcUaSecuritySettings security, string opcUrl)
        {
            if (offered == null)
            {
                return null;
            }

            result.Endpoints = offered
                .Select(e => new OpcUaEndpointInfo
                {
                    SecurityMode = e.SecurityMode.ToString(),
                    SecurityPolicy = OpcUaEndpointSelector.PolicyName(e.SecurityPolicyUri),
                    SecurityLevel = e.SecurityLevel,
                    EndpointUrl = e.EndpointUrl ?? ""
                })
                .ToList();

            EndpointDescription chosen = null;
            if (!security.IsUnsecured)
            {
                try
                {
                    chosen = OpcUaEndpointSelector.Select(offered, security, opcUrl);
                }
                catch (ServiceResultException)
                {
                    // Reported by the session attempt that follows.
                }
            }

            var withCertificate = chosen ?? offered
                .Where(e => e.ServerCertificate is { Length: > 0 })
                .OrderByDescending(e => e.SecurityLevel)
                .FirstOrDefault();
            using var certificate = OpcUaCertificateInfo.FirstOfChain(withCertificate?.ServerCertificate);
            result.ServerCertificate = OpcUaCertificateInfo.From(certificate);
            // The name the server gives itself, for the trust question before any session
            var server = withCertificate?.Server ?? offered.FirstOrDefault()?.Server;
            result.ApplicationName = server?.ApplicationName?.Text ?? "";
            result.ApplicationUri = server?.ApplicationUri ?? "";
            return chosen;
        }

        /// <summary>
        /// The certificate DataLogXE presents to OPC UA servers (created on first use); servers that require security
        /// must trust it.
        /// </summary>
        public static X509Certificate2 GetClientCertificate(string appName)
        {
            var config = CreateClientConfiguration(appName, 15000, appName, null);
            var certificate = config.SecurityConfiguration.ApplicationCertificate.Certificate;
            return certificate == null ? null : X509CertificateLoader.LoadCertificate(certificate.RawData);
        }

        // Further information for the connection test popup; every value is best effort.
        private static void ReadConnectionDetails(Session session, string username, OpcUaConnectionTestResult result)
        {
            var endpoint = session.Endpoint;
            result.EndpointUrl = endpoint?.EndpointUrl ?? "";
            result.SecurityPolicy = SecurityPolicies.GetDisplayName(endpoint?.SecurityPolicyUri) ?? endpoint?.SecurityPolicyUri ?? "";
            result.SecurityMode = endpoint?.SecurityMode.ToString() ?? "";
            result.UserIdentity = string.IsNullOrEmpty(username) ? "Anonymous" : $"User {username}";
            result.ApplicationName = endpoint?.Server?.ApplicationName?.Text ?? "";
            result.ApplicationUri = endpoint?.Server?.ApplicationUri ?? "";
            result.ManufacturerName = TryReadValue(session, Variables.Server_ServerStatus_BuildInfo_ManufacturerName) as string ?? "";
            result.SoftwareVersion = TryReadValue(session, Variables.Server_ServerStatus_BuildInfo_SoftwareVersion) as string ?? "";
            result.BuildNumber = TryReadValue(session, Variables.Server_ServerStatus_BuildInfo_BuildNumber) as string ?? "";
            if (TryReadValue(session, Variables.Server_ServerStatus_StartTime) is DateTime start && start != DateTime.MinValue)
            {
                result.StartTimeUtc = start.ToUniversalTime();
            }
            if (TryReadValue(session, Variables.Server_ServerStatus_State) is int state)
            {
                result.ServerState = ((ServerState)state).ToString();
            }
            if (TryReadValue(session, Variables.Server_NamespaceArray) is string[] namespaces)
            {
                result.NamespaceCount = namespaces.Length;
            }
            result.MaxNodesPerRead = ReadUInt(session, Variables.Server_ServerCapabilities_OperationLimits_MaxNodesPerRead);
            result.MaxNodesPerBrowse = ReadUInt(session, Variables.Server_ServerCapabilities_OperationLimits_MaxNodesPerBrowse);
            result.MaxMonitoredItemsPerCall = ReadUInt(session, Variables.Server_ServerCapabilities_OperationLimits_MaxMonitoredItemsPerCall);
            result.CurrentSessionCount = ReadUInt(session, Variables.Server_ServerDiagnostics_ServerDiagnosticsSummary_CurrentSessionCount);
            result.CurrentSubscriptionCount = ReadUInt(session, Variables.Server_ServerDiagnostics_ServerDiagnosticsSummary_CurrentSubscriptionCount);
            try
            {
                var read = new ReadValueIdCollection { new ReadValueId { NodeId = ObjectIds.Server, AttributeId = Attributes.EventNotifier } };
                session.Read(null, 0, TimestampsToReturn.Neither, read, out var values, out _);
                if (values.Count > 0 && values[0].Value is byte bits)
                {
                    result.EventsSupported = (bits & EventNotifiers.SubscribeToEvents) != 0;
                }
            }
            catch
            {
                // Not reported.
            }
        }

        // An unsigned limit or counter; null when missing, 0 means "no limit" for the operation limits.
        private static long? ReadUInt(Session session, uint nodeIdentifier)
        {
            var raw = TryReadValue(session, nodeIdentifier);
            return raw switch
            {
                uint u => u,
                ushort us => us,
                int i => i,
                _ => null
            };
        }

        /// <summary>
        /// Server clock minus local clock in ms: Server.ServerStatus.CurrentTime against the middle of the read;
        /// null when the server does not report it.
        /// </summary>
        private static long? ReadClockOffsetMs(Session session)
        {
            var before = DateTime.UtcNow;
            if (TryReadValue(session, Variables.Server_ServerStatus_CurrentTime) is not DateTime serverTime
                || serverTime == DateTime.MinValue)
            {
                return null;
            }
            var after = DateTime.UtcNow;
            var local = before + TimeSpan.FromTicks((after - before).Ticks / 2);
            return (long)(serverTime.ToUniversalTime() - local).TotalMilliseconds;
        }

        /// <summary>
        /// Reads Server.ServerCapabilities.MinSupportedSampleRate (a Duration in ms) rounded up to whole ms; null when
        /// the server does not have the variable or reports no valid value.
        /// </summary>
        private static int? ReadMinSupportedSampleRate(Session session)
        {
            try
            {
                // Convert.ToDouble(null) would be 0, which is a valid rate.
                var raw = TryReadValue(session, Variables.Server_ServerCapabilities_MinSupportedSampleRate);
                if (raw == null)
                {
                    return null;
                }

                var value = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (double.IsNaN(value) || value < 0)
                {
                    return null;
                }

                return (int)Math.Min(Math.Ceiling(value), int.MaxValue);
            }
            catch
            {
                return null;
            }
        }

        private static object TryReadValue(Session session, uint nodeIdentifier)
        {
            try
            {
                return session.ReadValue(new NodeId(nodeIdentifier)).Value;
            }
            catch
            {
                return null;
            }
        }

        private void UpdateNamespaceArray()
        {
            _namespaceArray.Clear();

            // read all namespace uris and add the uri to the list
            for (uint i = 0; i < _serverSession.NamespaceUris.Count; i++)
            {
                _namespaceArray.Add(_serverSession.NamespaceUris.GetString(i));
            }
        }

        private void UpdateServerCapabilities()
        {
            // 0 means no limit (Part 5); the chunks of monitored items still need a size.
            MaxNodesPerRegisterNodes = TryReadOperationLimit(
                Variables.Server_ServerCapabilities_OperationLimits_MaxNodesPerRegisterNodes, 100);

            MaxNodesPerBrowse = TryReadOperationLimit(
                Variables.Server_ServerCapabilities_OperationLimits_MaxNodesPerBrowse, 100);

            MaxNodesPerRead = TryReadOperationLimit(
                Variables.Server_ServerCapabilities_OperationLimits_MaxNodesPerRead, 1000);

            MinSupportedSampleRate = ReadMinSupportedSampleRate(_serverSession);
        }

        protected string GetNamespaceUriByNodeId(NodeId nodeId)
        {
            return _namespaceArray[nodeId.NamespaceIndex];
        }

        #endregion

        #region read / write values
        private int ReadIntByNodeIdentifier(uint nodeIdentifier)
        {
            return ReadIntByNodeIdentifier(_serverSession, nodeIdentifier);
        }

        private int TryReadOperationLimit(uint nodeIdentifier, int fallback)
        {
            return TryReadOperationLimit(_serverSession, nodeIdentifier, fallback);
        }

        private static int TryReadOperationLimit(Session session, uint nodeIdentifier, int fallback)
        {
            try
            {
                var value = ReadIntByNodeIdentifier(session, nodeIdentifier);
                if (value <= 0)
                {
                    return fallback;
                }

                return Math.Min(value, 5000);
            }
            catch
            {
                return fallback;
            }
        }

        private static int ReadIntByNodeIdentifier(Session session, uint nodeIdentifier)
        {
            NodeId nodeId = new NodeId(nodeIdentifier);
            var value = session.ReadValue(nodeId);
            return Convert.ToInt32(value.Value);
        }
        #endregion

        #region event handling
        public event EventHandler<ClientMessageEventArgs> OnClientMessage;
        protected void DoOnClientMessage(string message)
        {
            OnClientMessage?.Invoke(this, new ClientMessageEventArgs(message));
        }

        public event EventHandler<ClientMessageEventArgs> OnClientError;
        protected void DoOnClientError(string error)
        {
            OnClientError?.Invoke(this, new ClientMessageEventArgs(error));
        }

        public event EventHandler<StateChangeEventArgs> OnStateChange;
        private void DoOnStateChange(ConnectionState state)
        {
            OnStateChange?.Invoke(this, new StateChangeEventArgs(state));
        }

        public event EventHandler<ServerKeepAliveEventArgs> OnServerKeepAlive;
        private void DoOnServerKeepAlive(KeepAliveEventArgs e)
        {
            // fire event only in server running state
            if (e.CurrentState == ServerState.Running)
                OnServerKeepAlive?.Invoke(this, new ServerKeepAliveEventArgs(e));
        }
        #endregion
    }

    public class OpcUaBrowser : OpcUaClientBase
    {
        #region member
        // Parent batch and child page size are independent OPC UA limits.
        // Keep the product under typical Siemens / 512 KB message budgets.
        private const int MaxParentNodesPerBrowseCall = 20;
        // References one Browse call asks for in all, split over its parents (at least MinReferencesPerNode each).
        // A parent that has more keeps no continuation point of the shared call: it is browsed again on its own with
        // the whole budget, so a node with thousands of children takes few BrowseNext calls. A response the server
        // or the client cannot carry is a capacity error and halves the page.
        private const uint ReferencesPerBrowseCall = 1000;
        private const uint MinReferencesPerNode = 32;
        internal const int MaxArrayElements = OpcUaArrayElements.MaxElements;
        private const int VariableClassifyChunkSize = 512;

        private readonly object _browseLock = new object();
        private int _browseSequence = 0;
        private HashSet<NodeId> _browseVisited;
        private Dictionary<NodeId, OpcUaDataTypeKind> _browseTypeCache;
        // Progress of the running walk. The deep calls (continuation points of a node with thousands of children,
        // the type reads of a big chunk) report through it, so the status moves while one step takes minutes.
        private Action<OpcUaBrowseProgress> _walkProgress;
        private OpcUaAuxSession _walkSession;
        private DateTime _walkProgressUtc;
        private int _walkNodes;
        private int _walkTags;
        #endregion

        #region properties
        public int LastBrowseFailures { get; private set; }
        public int LastStructuresExpanded { get; private set; }
        public bool LastStructureExpansionLimited { get; private set; }
        /// <summary>Arrays of the last browse with more than <see cref="MaxArrayElements"/> elements, cut there.</summary>
        public int LastArraysTruncated { get; private set; }
        private int _arrayFailures;
        public Action<string> OnUnresolvedBrowseNodeKey { get; set; }
        private int _unresolvedBrowseNodeKeyLogBudget;
        #endregion

        internal enum OpcUaDataTypeKind
        {
            Boolean,
            Number,
            Structure,
            Rejected
        }

        #region find node by string identifier
        public async Task<ExpandedNodeId> FindNodeByStringIdentifierAsync(string identifier)
        {
            return await Task.Run(() => FindNodeByStringIdentifier(identifier));
        }

        public ExpandedNodeId FindNodeByStringIdentifier(string identifier)
        {
            if (identifier != "")
            {
                FindNodeByStringIdentifierRecoursive(identifier, ObjectIds.RootFolder, out NodeId resultNodeId);
                if (resultNodeId != null)
                    return new ExpandedNodeId(resultNodeId, GetNamespaceUriByNodeId(resultNodeId));
            }
            else
            {
                return new ExpandedNodeId(ObjectIds.RootFolder, GetNamespaceUriByNodeId(ObjectIds.RootFolder));
            }
            return null;
        }

        private void FindNodeByStringIdentifierRecoursive(string identifier, NodeId actualNode, out NodeId result)
        {
            result = null;

            if (actualNode.Identifier.Equals(identifier))
            {
                result = actualNode;
            }
            else
            {
                foreach (var child in BrowseNodeId(actualNode))
                {
                    FindNodeByStringIdentifierRecoursive(identifier, child, out result);
                    if (result != null) break;
                }
            }
        }
        #endregion

        #region browse node
        public async Task BrowseNodeAsync(ExpandedNodeId expNodeId)
        {
            await Task.Run(() => BrowseNode(expNodeId));
        }

        public void BrowseNode(ExpandedNodeId expNodeId)
        {
            var nodeId = ExpandedNodeId.ToNodeId(expNodeId, _serverSession.NamespaceUris);
            _browseSequence = 0;
            BrowseNodeRecoursive(nodeId);
        }

        private void BrowseNodeRecoursive(NodeId actualNode)
        {
            var children = BrowseNodeId(actualNode);

            // get expanded node id from node id
            ExpandedNodeId expNodeId = new ExpandedNodeId(actualNode, GetNamespaceUriByNodeId(actualNode));

            // fire event to inform about the new node item
            DoOnBrowseItem(GetBrowseItem(expNodeId, children.Count > 0));

            // browse actual node´s children
            foreach (var child in children)
            {
                BrowseNodeRecoursive(child);
            }
        }

        private BrowseResultItem GetBrowseItem(ExpandedNodeId expNodeId, bool hasChildren)
        {
            return new BrowseResultItem { ExpNodeId = expNodeId, HasChildren = hasChildren, Sequence = _browseSequence++ };
        }
        #endregion 

        #region browse helper
        private List<NodeId> BrowseNodeId(NodeId nodeId)
        {
            // browse from starting point
            _serverSession.Browse(
                null,
                null,
                nodeId,
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object,
                out byte[] continuationPoint,
                out ReferenceDescriptionCollection refCollection);

            while (continuationPoint != null)
            {
                _serverSession.BrowseNext(
                    null,
                    false,
                    continuationPoint,
                    out byte[] revisedContinuationPoint,
                    out ReferenceDescriptionCollection nextRefCollection);

                refCollection.AddRange(nextRefCollection);
                continuationPoint = revisedContinuationPoint;
            }

            var result = new List<NodeId>();
            foreach (var rd in refCollection)
                result.Add(ExpandedNodeId.ToNodeId(rd.NodeId, _serverSession.NamespaceUris));

            return result;
        }
        #endregion

        #region browse scalar variables
        internal void ResetBrowseCache()
        {
            lock (_browseLock)
            {
                _browseTypeCache = null;
                _browseVisited = null;
            }
        }

        internal void ClearBrowseCaches(IReadOnlyList<OpcUaAuxSession> sessions = null)
        {
            ResetBrowseCache();
            ClearSessionNodeCache(_serverSession);
            if (sessions == null)
            {
                return;
            }

            foreach (var session in sessions)
            {
                ClearSessionNodeCache(session?.Session);
            }
        }

        private static void ClearSessionNodeCache(Session session)
        {
            try
            {
                session?.NodeCache?.Clear();
            }
            catch
            {
                // Clearing the node cache is best-effort.
            }
        }

        public void BrowseVariableNodes(
            OpcUaAuxSession session,
            Action<List<OpcUaScalarVariable>> onScalars,
            Action<OpcUaBrowseProgress> progress,
            CancellationToken cancellationToken,
            IOpcUaBrowseStore store,
            BrowseCheckpointState resume)
        {
            EnsureAuxSession(session);
            ArgumentNullException.ThrowIfNull(onScalars);
            ArgumentNullException.ThrowIfNull(store);

            lock (_browseLock)
            {
                _browseVisited = new HashSet<NodeId>();
                _browseTypeCache ??= new Dictionary<NodeId, OpcUaDataTypeKind>();
                _unresolvedBrowseNodeKeyLogBudget = 10;
                LastBrowseFailures = 0;
                LastStructuresExpanded = 0;
                LastStructureExpansionLimited = false;
                LastArraysTruncated = 0;
                _arrayFailures = 0;

                var queue = new Queue<NodeId>();
                var variables = new List<OpcUaVariableNode>(VariableClassifyChunkSize);
                var rootFailures = 0;
                var scalarsFound = resume?.FoundCount ?? 0;
                if (ShouldRestoreBrowseCheckpoint(resume))
                {
                    RestoreBrowseState(session, resume, queue, variables);
                }
                else
                {
                    var objectsKey = ToAddress(session, ObjectIds.ObjectsFolder);
                    store.SetInflight("seed", [objectsKey]);
                    var plc = FindPlcInstance(session, cancellationToken, ref rootFailures);
                    var root = plc ?? ObjectIds.ObjectsFolder;
                    var rootKey = ToAddress(session, root);
                    store.CommitBrowse([objectsKey], [rootKey], []);
                    queue.Enqueue(root);
                    _browseVisited.Add(root);
                }

                var lastProgressUtc = DateTime.MinValue;
                ReportProgress(progress, ref lastProgressUtc, true, new OpcUaBrowseProgress
                {
                    Phase = "browse",
                    NodesBrowsed = _browseVisited.Count,
                    ScalarTags = scalarsFound
                });

                _walkProgress = progress;
                _walkSession = session;
                _walkProgressUtc = lastProgressUtc;
                try
                {
                    CollectVariablesFromQueue(
                        session, queue, variables, onScalars, progress, ref lastProgressUtc, ref scalarsFound, cancellationToken, store);
                }
                finally
                {
                    _walkProgress = null;
                    _walkSession = null;
                }
                if (rootFailures > 0)
                {
                    LastBrowseFailures += rootFailures;
                }
            }
        }

        private static bool ShouldRestoreBrowseCheckpoint(BrowseCheckpointState resume)
        {
            if (resume == null || !resume.Exists)
            {
                return false;
            }

            if (!string.Equals(resume.Phase, "seed", StringComparison.Ordinal))
            {
                return true;
            }

            return (resume.Queue?.Count ?? 0) > 0
                || (resume.Inflight?.Count ?? 0) > 0
                || (resume.Pending?.Count ?? 0) > 0
                || resume.FoundCount > 0;
        }

        private void RestoreBrowseState(
            OpcUaAuxSession session,
            BrowseCheckpointState resume,
            Queue<NodeId> queue,
            List<OpcUaVariableNode> variables)
        {
            var inflight = new HashSet<string>(resume.Inflight ?? []);
            foreach (var key in resume.Visited ?? [])
            {
                var nodeId = ResolveNodeKey(session, key);
                if (nodeId != null)
                {
                    _browseVisited.Add(nodeId);
                }
                else
                {
                    ReportUnresolvedBrowseNodeKey(key);
                }
            }

            var classifyRetry = string.Equals(resume.Phase, "classify", StringComparison.Ordinal) && inflight.Count > 0;
            foreach (var pending in resume.Pending ?? [])
            {
                var nodeId = ResolveNodeKey(session, pending?.NodeKey);
                if (nodeId == null)
                {
                    ReportUnresolvedBrowseNodeKey(pending?.NodeKey);
                    continue;
                }

                if (classifyRetry && !inflight.Contains(pending.NodeKey))
                {
                    continue;
                }

                variables.Add(new OpcUaVariableNode
                {
                    NodeId = nodeId,
                    DisplayName = pending.DisplayName,
                    BrowseName = pending.BrowseName
                });
            }

            var resumeBrowse = string.Equals(resume.Phase, "browse", StringComparison.Ordinal)
                || string.Equals(resume.Phase, "seed", StringComparison.Ordinal);
            if (resumeBrowse)
            {
                foreach (var key in resume.Inflight ?? [])
                {
                    var nodeId = ResolveNodeKey(session, key);
                    if (nodeId != null)
                    {
                        queue.Enqueue(nodeId);
                    }
                    else
                    {
                        ReportUnresolvedBrowseNodeKey(key);
                    }
                }
            }

            foreach (var key in resume.Queue ?? [])
            {
                if (inflight.Contains(key) && resumeBrowse)
                {
                    continue;
                }

                var nodeId = ResolveNodeKey(session, key);
                if (nodeId != null)
                {
                    queue.Enqueue(nodeId);
                }
                else
                {
                    ReportUnresolvedBrowseNodeKey(key);
                }
            }
        }

        private void ReportUnresolvedBrowseNodeKey(string nodeKey)
        {
            if (string.IsNullOrWhiteSpace(nodeKey) || _unresolvedBrowseNodeKeyLogBudget <= 0)
            {
                return;
            }

            _unresolvedBrowseNodeKeyLogBudget--;
            OnUnresolvedBrowseNodeKey?.Invoke(nodeKey);
        }

        private static NodeId ResolveNodeKey(OpcUaAuxSession session, string nodeKey)
        {
            if (string.IsNullOrWhiteSpace(nodeKey))
            {
                return null;
            }

            try
            {
                var expanded = ExpandedNodeId.Parse(nodeKey);
                return ExpandedNodeId.ToNodeId(expanded, session.Session.NamespaceUris);
            }
            catch
            {
                return null;
            }
        }

        private NodeId FindPlcInstance(OpcUaAuxSession session, CancellationToken cancellationToken, ref int failures)
        {
            var references = BrowseMany(
                session,
                new List<NodeId> { ObjectIds.ObjectsFolder },
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Object,
                (uint)BrowseResultMask.BrowseName,
                ReferencesPerBrowseCall,
                cancellationToken,
                ref failures);

            foreach (var reference in references)
            {
                var childId = ToSessionNodeId(session, reference.NodeId);
                if (childId != null
                    && childId.IdType == IdType.String
                    && string.Equals(childId.Identifier as string, "PLC", StringComparison.Ordinal))
                {
                    return childId;
                }
            }

            return null;
        }

        private static void EnsureAuxSession(OpcUaAuxSession session)
        {
            if (session?.Session == null || !session.Session.Connected)
            {
                throw new InvalidOperationException("The browse session is not connected.");
            }
        }

        private void CollectVariablesFromQueue(
            OpcUaAuxSession session,
            Queue<NodeId> queue,
            List<OpcUaVariableNode> variables,
            Action<List<OpcUaScalarVariable>> onScalars,
            Action<OpcUaBrowseProgress> progress,
            ref DateTime lastProgressUtc,
            ref int scalarsFound,
            CancellationToken cancellationToken,
            IOpcUaBrowseStore store)
        {
            var scalars = new List<OpcUaScalarVariable>(VariableClassifyChunkSize);
            var expandables = new Queue<NodeId>();
            var batchesSinceTrim = 0;
            var nodesBrowsed = _browseVisited.Count;
            var failures = 0;
            var structuresExpanded = 0;

            while (queue.Count > 0 || variables.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _walkNodes = nodesBrowsed;
                _walkTags = scalarsFound;

                if (queue.Count == 0)
                {
                    ClassifyAndEnqueueStructures(
                        session, queue, variables, expandables, scalars, onScalars,
                        ref scalarsFound, ref structuresExpanded, cancellationToken, store);
                    ClearSessionNodeCache(session.Session);
                    batchesSinceTrim = 0;
                    continue;
                }

                var batch = DequeueBatch(queue, GetParentBrowseBatchSize(session));
                var batchKeys = batch.ConvertAll(nodeId => ToAddress(session, nodeId));
                store.SetInflight("browse", batchKeys);
                var references = BrowseMany(
                    session,
                    batch,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    (uint)NodeClass.Object | (uint)NodeClass.Variable,
                    (uint)(BrowseResultMask.NodeClass | BrowseResultMask.BrowseName | BrowseResultMask.DisplayName | BrowseResultMask.TypeDefinition),
                    ReferencesPerBrowseCall,
                    cancellationToken,
                    ref failures);
                nodesBrowsed += batch.Count;

                var newObjects = new List<string>();
                var newVariables = new List<BrowseCheckpointNode>();
                foreach (var reference in references)
                {
                    var childId = ToSessionNodeId(session, reference.NodeId);
                    if (childId == null || IsSkippedObject(childId)
                        || (reference.NodeClass == NodeClass.Object
                            && IsProgramAlarmInstance(reference.TypeDefinition, session.Session.NamespaceUris))
                        || !_browseVisited.Add(childId))
                    {
                        continue;
                    }

                    var childKey = ToAddress(session, childId);
                    if (reference.NodeClass == NodeClass.Variable)
                    {
                        variables.Add(new OpcUaVariableNode
                        {
                            NodeId = childId,
                            DisplayName = reference.DisplayName?.Text,
                            BrowseName = reference.BrowseName?.Name
                        });
                        newVariables.Add(new BrowseCheckpointNode
                        {
                            NodeKey = childKey,
                            DisplayName = reference.DisplayName?.Text,
                            BrowseName = reference.BrowseName?.Name
                        });
                    }
                    else if (reference.NodeClass == NodeClass.Object)
                    {
                        queue.Enqueue(childId);
                        newObjects.Add(childKey);
                    }
                }

                store.CommitBrowse(batchKeys, newObjects, newVariables);

                if (variables.Count >= VariableClassifyChunkSize)
                {
                    ClassifyAndEnqueueStructures(
                        session, queue, variables, expandables, scalars, onScalars,
                        ref scalarsFound, ref structuresExpanded, cancellationToken, store);
                    ClearSessionNodeCache(session.Session);
                    batchesSinceTrim = 0;
                }
                else
                {
                    batchesSinceTrim++;
                    if (batchesSinceTrim >= 25)
                    {
                        ClearSessionNodeCache(session.Session);
                        batchesSinceTrim = 0;
                    }
                }

                ReportProgress(progress, ref lastProgressUtc, false, new OpcUaBrowseProgress
                {
                    Phase = "browse",
                    NodesBrowsed = nodesBrowsed,
                    VariablesFound = scalarsFound,
                    ScalarTags = scalarsFound,
                    StructuresExpanded = structuresExpanded,
                    BrowseFailures = failures
                });
            }

            LastBrowseFailures = failures + _arrayFailures;
            LastStructuresExpanded = structuresExpanded;
            ReportProgress(progress, ref lastProgressUtc, true, new OpcUaBrowseProgress
            {
                Phase = "browse",
                NodesBrowsed = nodesBrowsed,
                VariablesFound = scalarsFound,
                ScalarTags = scalarsFound,
                StructuresExpanded = structuresExpanded,
                BrowseFailures = failures
            });
        }

        private void ClassifyAndEnqueueStructures(
            OpcUaAuxSession session,
            Queue<NodeId> queue,
            List<OpcUaVariableNode> variables,
            Queue<NodeId> expandables,
            List<OpcUaScalarVariable> scalars,
            Action<List<OpcUaScalarVariable>> onScalars,
            ref int scalarsFound,
            ref int structuresExpanded,
            CancellationToken cancellationToken,
            IOpcUaBrowseStore store)
        {
            if (variables.Count == 0)
            {
                return;
            }

            ClassifyVariableChunk(session, variables, expandables, scalars, cancellationToken, store);
            variables.Clear();

            while (expandables.Count > 0)
            {
                var expandable = expandables.Dequeue();
                if (expandable == null)
                {
                    continue;
                }

                queue.Enqueue(expandable);
                structuresExpanded++;
            }

            if (scalars.Count == 0)
            {
                return;
            }

            onScalars(scalars);
            scalarsFound += scalars.Count;
            scalars.Clear();
        }

        private void ClassifyVariableChunk(
            OpcUaAuxSession session,
            List<OpcUaVariableNode> variables,
            Queue<NodeId> expandables,
            List<OpcUaScalarVariable> scalars,
            CancellationToken cancellationToken,
            IOpcUaBrowseStore store)
        {
            if (variables.Count == 0)
            {
                return;
            }

            var lastProgressUtc = DateTime.MinValue;
            var offset = 0;
            while (offset < variables.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var size = Math.Min(variables.Count - offset, Math.Max(1, session.MaxNodesPerRead / 3));
                var chunk = variables.GetRange(offset, size);
                var finished = new List<string>(chunk.Count);
                foreach (var variable in chunk)
                {
                    finished.Add(ToAddress(session, variable.NodeId));
                }

                store.SetInflight("classify", finished);
                var reads = ReadVariableTypeChunk(session, chunk, cancellationToken);
                PrefetchSuperTypes(session, reads, null, ref lastProgressUtc, cancellationToken);
                var chunkExpandables = new Queue<NodeId>();
                var chunkScalars = new List<OpcUaScalarVariable>();
                var chunkArrays = new List<VariableTypeRead>();
                foreach (var read in reads)
                {
                    ClassifyRead(session, read, chunkExpandables, chunkScalars, chunkArrays);
                }

                var renames = new List<KeyValuePair<string, string>>();
                ExpandArrays(session, chunkArrays, chunkScalars, renames, cancellationToken);
                if (renames.Count > 0)
                {
                    store.RenameTags(renames);
                }

                var newObjects = new List<string>(chunkExpandables.Count);
                foreach (var expandable in chunkExpandables)
                {
                    newObjects.Add(ToAddress(session, expandable));
                    expandables.Enqueue(expandable);
                }

                var imports = new List<DataTagBrowseImportItem>(chunkScalars.Count);
                foreach (var scalar in chunkScalars)
                {
                    imports.Add(new DataTagBrowseImportItem
                    {
                        Address = scalar.Address,
                        Name = scalar.Name,
                        DisplayName = scalar.DisplayName,
                        TypeId = scalar.TypeId
                    });
                    scalars.Add(scalar);
                }

                store.CommitClassify(finished, newObjects, imports);
                offset += size;
                _walkTags += chunkScalars.Count;
                ReportWalk(session, $"types {offset}/{variables.Count}");
            }
        }

        private static int GetParentBrowseBatchSize(OpcUaAuxSession session)
        {
            return Math.Min(MaxParentNodesPerBrowseCall, Math.Max(1, session.MaxNodesPerBrowse));
        }

        private static List<NodeId> DequeueBatch(Queue<NodeId> queue, int chunkSize)
        {
            var batch = new List<NodeId>(Math.Min(chunkSize, queue.Count));
            while (queue.Count > 0 && batch.Count < chunkSize)
            {
                batch.Add(queue.Dequeue());
            }

            return batch;
        }

        private List<ReferenceDescription> BrowseMany(
            OpcUaAuxSession aux,
            List<NodeId> nodeIds,
            BrowseDirection direction,
            NodeId referenceTypeId,
            bool includeSubtypes,
            uint nodeClassMask,
            uint resultMask,
            uint maxReferencesPerNode,
            CancellationToken cancellationToken,
            ref int failures)
        {
            var collected = new List<ReferenceDescription>();
            if (nodeIds == null || nodeIds.Count == 0)
            {
                return collected;
            }

            maxReferencesPerNode = Math.Max(1, maxReferencesPerNode);
            var perNode = ReferencesPerNode(maxReferencesPerNode, nodeIds.Count);

            try
            {
                var descriptions = new BrowseDescriptionCollection();
                foreach (var nodeId in nodeIds)
                {
                    descriptions.Add(new BrowseDescription
                    {
                        NodeId = nodeId,
                        BrowseDirection = direction,
                        ReferenceTypeId = referenceTypeId,
                        IncludeSubtypes = includeSubtypes,
                        NodeClassMask = nodeClassMask,
                        ResultMask = resultMask
                    });
                }

                aux.Session.Browse(
                    null,
                    null,
                    perNode,
                    descriptions,
                    out BrowseResultCollection results,
                    out _);

                var continuationPoints = new ByteStringCollection();
                var retryNodes = new List<NodeId>();
                var releasePoints = new ByteStringCollection();
                var largeNodes = perNode < maxReferencesPerNode ? new List<NodeId>() : null;
                CollectBrowseResults(
                    results, nodeIds, collected, continuationPoints, retryNodes, releasePoints, ref failures, largeNodes);
                ReleaseContinuationPoints(aux, releasePoints);
                FollowBrowseContinuationPoints(
                    aux, continuationPoints, collected, cancellationToken, ref failures);
                FollowCapacityRetries(
                    aux, retryNodes, direction, referenceTypeId, includeSubtypes,
                    nodeClassMask, resultMask, maxReferencesPerNode, collected, cancellationToken, ref failures);
                foreach (var largeNode in largeNodes ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    collected.AddRange(BrowseMany(
                        aux, [largeNode], direction, referenceTypeId, includeSubtypes,
                        nodeClassMask, resultMask, maxReferencesPerNode, cancellationToken, ref failures));
                }

                return collected;
            }
            catch (OpcUaBrowseInterruptedException)
            {
                throw;
            }
            catch (Exception ex) when (IsSessionLost(ex, aux.Session))
            {
                throw new OpcUaBrowseInterruptedException("The OPC UA browse session was lost.", ex);
            }
            catch (Exception ex) when (IsCapacityError(ex) && nodeIds.Count > 1)
            {
                var mid = nodeIds.Count / 2;
                var left = BrowseMany(
                    aux, nodeIds.GetRange(0, mid), direction, referenceTypeId, includeSubtypes,
                    nodeClassMask, resultMask, maxReferencesPerNode, cancellationToken, ref failures);
                var right = BrowseMany(
                    aux, nodeIds.GetRange(mid, nodeIds.Count - mid), direction, referenceTypeId, includeSubtypes,
                    nodeClassMask, resultMask, maxReferencesPerNode, cancellationToken, ref failures);
                left.AddRange(right);
                return left;
            }
            catch (Exception ex) when (IsCapacityError(ex) && maxReferencesPerNode > 1)
            {
                var reduced = Math.Max(1u, maxReferencesPerNode / 2);
                return BrowseMany(
                    aux, nodeIds, direction, referenceTypeId, includeSubtypes,
                    nodeClassMask, resultMask, reduced, cancellationToken, ref failures);
            }
            catch (Exception ex) when (IsCapacityError(ex))
            {
                failures += nodeIds.Count;
                return collected;
            }
        }

        internal static uint ReferencesPerNode(uint maxReferencesPerNode, int nodeCount)
        {
            if (nodeCount <= 1)
            {
                return maxReferencesPerNode;
            }

            var share = maxReferencesPerNode / (uint)nodeCount;
            return Math.Min(maxReferencesPerNode, Math.Max(MinReferencesPerNode, share));
        }

        private void FollowBrowseContinuationPoints(
            OpcUaAuxSession aux,
            ByteStringCollection continuationPoints,
            List<ReferenceDescription> collected,
            CancellationToken cancellationToken,
            ref int failures)
        {
            if (continuationPoints == null || continuationPoints.Count == 0)
            {
                return;
            }

            FollowBrowseContinuationPoints(
                aux, continuationPoints, 0, continuationPoints.Count, collected, cancellationToken, ref failures);
        }

        private void FollowBrowseContinuationPoints(
            OpcUaAuxSession aux,
            ByteStringCollection continuationPoints,
            int offset,
            int count,
            List<ReferenceDescription> collected,
            CancellationToken cancellationToken,
            ref int failures)
        {
            if (count <= 0)
            {
                return;
            }

            var batch = new ByteStringCollection();
            for (var i = 0; i < count; i++)
            {
                batch.Add(continuationPoints[offset + i]);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                aux.Session.BrowseNext(
                    null,
                    false,
                    batch,
                    out BrowseResultCollection nextResults,
                    out _);

                var nextContinuationPoints = new ByteStringCollection();
                var releasePoints = new ByteStringCollection();
                CollectBrowseResults(
                    nextResults, null, collected, nextContinuationPoints, null, releasePoints, ref failures);
                ReportWalk(aux, $"{collected.Count} children");
                ReleaseContinuationPoints(aux, releasePoints);
                FollowBrowseContinuationPoints(
                    aux, nextContinuationPoints, collected, cancellationToken, ref failures);
            }
            catch (OperationCanceledException)
            {
                ReleaseContinuationPoints(aux, batch);
                throw;
            }
            catch (OpcUaBrowseInterruptedException)
            {
                ReleaseContinuationPoints(aux, batch);
                throw;
            }
            catch (Exception ex) when (IsSessionLost(ex, aux.Session))
            {
                ReleaseContinuationPoints(aux, batch);
                throw new OpcUaBrowseInterruptedException("The OPC UA browse session was lost.", ex);
            }
            catch (Exception ex) when (IsCapacityError(ex) && count > 1)
            {
                var mid = count / 2;
                FollowBrowseContinuationPoints(
                    aux, continuationPoints, offset, mid, collected, cancellationToken, ref failures);
                FollowBrowseContinuationPoints(
                    aux, continuationPoints, offset + mid, count - mid, collected, cancellationToken, ref failures);
            }
            catch (Exception ex) when (IsCapacityError(ex))
            {
                ReleaseContinuationPoints(aux, batch);
                failures += count;
            }
            catch
            {
                ReleaseContinuationPoints(aux, batch);
                throw;
            }
        }

        private void FollowCapacityRetries(
            OpcUaAuxSession aux,
            List<NodeId> retryNodes,
            BrowseDirection direction,
            NodeId referenceTypeId,
            bool includeSubtypes,
            uint nodeClassMask,
            uint resultMask,
            uint maxReferencesPerNode,
            List<ReferenceDescription> collected,
            CancellationToken cancellationToken,
            ref int failures)
        {
            if (retryNodes == null || retryNodes.Count == 0)
            {
                return;
            }

            if (maxReferencesPerNode > 1)
            {
                collected.AddRange(BrowseMany(
                    aux, retryNodes, direction, referenceTypeId, includeSubtypes,
                    nodeClassMask, resultMask, Math.Max(1u, maxReferencesPerNode / 2),
                    cancellationToken, ref failures));
                return;
            }

            if (retryNodes.Count > 1)
            {
                var mid = retryNodes.Count / 2;
                collected.AddRange(BrowseMany(
                    aux, retryNodes.GetRange(0, mid), direction, referenceTypeId, includeSubtypes,
                    nodeClassMask, resultMask, maxReferencesPerNode, cancellationToken, ref failures));
                collected.AddRange(BrowseMany(
                    aux, retryNodes.GetRange(mid, retryNodes.Count - mid), direction, referenceTypeId, includeSubtypes,
                    nodeClassMask, resultMask, maxReferencesPerNode, cancellationToken, ref failures));
                return;
            }

            failures += retryNodes.Count;
        }

        private static void CollectBrowseResults(
            BrowseResultCollection results,
            IReadOnlyList<NodeId> sourceNodes,
            List<ReferenceDescription> collected,
            ByteStringCollection continuationPoints,
            List<NodeId> retryNodes,
            ByteStringCollection releasePoints,
            ref int failures,
            List<NodeId> largeNodes = null)
        {
            if (results == null)
            {
                return;
            }

            for (var i = 0; i < results.Count; i++)
            {
                var browseResult = results[i];
                if (browseResult == null)
                {
                    failures++;
                    continue;
                }

                if (StatusCode.IsBad(browseResult.StatusCode))
                {
                    if (browseResult.ContinuationPoint != null && browseResult.ContinuationPoint.Length > 0
                        && releasePoints != null)
                    {
                        releasePoints.Add(browseResult.ContinuationPoint);
                    }

                    if (IsTransportStatus(browseResult.StatusCode))
                    {
                        throw new OpcUaBrowseInterruptedException(
                            "The OPC UA browse session was lost.",
                            new ServiceResultException(browseResult.StatusCode));
                    }

                    if (retryNodes != null && sourceNodes != null && i < sourceNodes.Count
                        && IsCapacityStatus(browseResult.StatusCode))
                    {
                        retryNodes.Add(sourceNodes[i]);
                    }
                    else
                    {
                        failures++;
                    }

                    continue;
                }

                var more = browseResult.ContinuationPoint != null && browseResult.ContinuationPoint.Length > 0;
                if (more && largeNodes != null && sourceNodes != null && i < sourceNodes.Count)
                {
                    // Browsed again on its own with bigger pages; the first page comes again with it.
                    releasePoints?.Add(browseResult.ContinuationPoint);
                    largeNodes.Add(sourceNodes[i]);
                    continue;
                }

                if (browseResult.References != null)
                {
                    collected.AddRange(browseResult.References);
                }

                if (more)
                {
                    continuationPoints.Add(browseResult.ContinuationPoint);
                }
            }
        }
        private List<VariableTypeRead> ReadVariableTypeChunk(
            OpcUaAuxSession session,
            List<OpcUaVariableNode> chunk,
            CancellationToken cancellationToken)
        {
            if (chunk.Count == 0)
            {
                return [];
            }

            try
            {
                var nodesToRead = new ReadValueIdCollection();
                var nodeIds = new List<NodeId>(chunk.Count);
                foreach (var variable in chunk)
                {
                    var nodeId = variable?.NodeId;
                    nodeIds.Add(nodeId);
                    if (nodeId == null)
                    {
                        continue;
                    }

                    nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Attributes.DataType });
                    nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Attributes.ValueRank });
                    nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Attributes.ArrayDimensions });
                }

                if (nodesToRead.Count == 0)
                {
                    return chunk.ConvertAll(v => new VariableTypeRead { Variable = v });
                }

                session.Session.Read(
                    null,
                    0,
                    TimestampsToReturn.Neither,
                    nodesToRead,
                    out DataValueCollection values,
                    out _);

                var reads = new List<VariableTypeRead>(chunk.Count);
                if (values == null || values.Count < nodesToRead.Count)
                {
                    throw new OpcUaBrowseInterruptedException(
                        "The OPC UA read did not return all values.",
                        null);
                }

                var valueIndex = 0;
                for (var i = 0; i < chunk.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var nodeId = nodeIds[i];
                    if (nodeId == null)
                    {
                        reads.Add(new VariableTypeRead { Variable = chunk[i] });
                        continue;
                    }

                    var dataTypeValue = values[valueIndex++];
                    var valueRankValue = values[valueIndex++];
                    var arrayDimensionsValue = values[valueIndex++];
                    if (IsTransportStatus(dataTypeValue?.StatusCode ?? StatusCodes.Good)
                        || IsTransportStatus(valueRankValue?.StatusCode ?? StatusCodes.Good))
                    {
                        throw new OpcUaBrowseInterruptedException(
                            "The OPC UA browse session was lost.",
                            new ServiceResultException(dataTypeValue?.StatusCode ?? valueRankValue.StatusCode));
                    }

                    var read = new VariableTypeRead { Variable = chunk[i] };
                    if (dataTypeValue != null && !StatusCode.IsBad(dataTypeValue.StatusCode)
                        && TryGetValueRank(valueRankValue, out var valueRank))
                    {
                        read.ValueRank = valueRank;
                        read.DataTypeId = dataTypeValue.Value as NodeId
                            ?? ToSessionNodeId(session, dataTypeValue.Value as ExpandedNodeId);
                        read.Ok = read.DataTypeId != null;
                        // Optional attribute: missing, null or 0 for a dimension means the length is not fixed.
                        if (arrayDimensionsValue != null && !StatusCode.IsBad(arrayDimensionsValue.StatusCode))
                        {
                            read.ArrayDimensions = arrayDimensionsValue.Value as uint[];
                        }
                    }

                    reads.Add(read);
                }

                return reads;
            }
            catch (OpcUaBrowseInterruptedException)
            {
                throw;
            }
            catch (Exception ex) when (IsSessionLost(ex, session.Session))
            {
                throw new OpcUaBrowseInterruptedException("The OPC UA browse session was lost.", ex);
            }
            catch (Exception ex) when (IsCapacityError(ex) && chunk.Count > 1)
            {
                var mid = chunk.Count / 2;
                var left = ReadVariableTypeChunk(session, chunk.GetRange(0, mid), cancellationToken);
                var right = ReadVariableTypeChunk(session, chunk.GetRange(mid, chunk.Count - mid), cancellationToken);
                left.AddRange(right);
                return left;
            }
            catch (Exception ex) when (IsCapacityError(ex))
            {
                return chunk.ConvertAll(v => new VariableTypeRead { Variable = v });
            }
        }

        private void PrefetchSuperTypes(
            OpcUaAuxSession session,
            List<VariableTypeRead> reads,
            Action<OpcUaBrowseProgress> progress,
            ref DateTime lastProgressUtc,
            CancellationToken cancellationToken)
        {
            _browseTypeCache ??= new Dictionary<NodeId, OpcUaDataTypeKind>();
            var pending = new List<(NodeId Original, NodeId Current)>();
            var seenOriginal = new HashSet<NodeId>();
            foreach (var read in reads)
            {
                if (!read.Ok || read.DataTypeId == null || !seenOriginal.Add(read.DataTypeId))
                {
                    continue;
                }

                if (_browseTypeCache.ContainsKey(read.DataTypeId) || ClassifyKnownDataType(read.DataTypeId) != null)
                {
                    continue;
                }

                pending.Add((read.DataTypeId, read.DataTypeId));
            }

            var typesToResolve = pending.Count;
            ReportProgress(progress, ref lastProgressUtc, true, new OpcUaBrowseProgress
            {
                Phase = "prefetch",
                VariablesFound = typesToResolve,
                NodesBrowsed = 0
            });

            for (var depth = 0; depth < 16 && pending.Count > 0; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uniqueCurrent = new List<NodeId>();
                var uniqueSet = new HashSet<NodeId>();
                foreach (var item in pending)
                {
                    if (uniqueSet.Add(item.Current))
                    {
                        uniqueCurrent.Add(item.Current);
                    }
                }

                var failures = 0;
                var supers = new Dictionary<NodeId, NodeId>();
                foreach (var batch in SplitList(uniqueCurrent, Math.Max(1, session.MaxNodesPerBrowse)))
                {
                    var referencesBySource = BrowseSuperTypes(session, batch, cancellationToken, ref failures);
                    foreach (var pair in referencesBySource)
                    {
                        supers[pair.Key] = pair.Value;
                    }
                }

                var next = new List<(NodeId Original, NodeId Current)>();
                foreach (var item in pending)
                {
                    if (!supers.TryGetValue(item.Current, out var super) || super == null)
                    {
                        _browseTypeCache[item.Original] = OpcUaDataTypeKind.Structure;
                        continue;
                    }

                    var kind = ClassifyKnownDataType(super);
                    if (kind != null)
                    {
                        _browseTypeCache[item.Original] = kind.Value;
                        continue;
                    }

                    if (_browseTypeCache.TryGetValue(super, out var cached))
                    {
                        _browseTypeCache[item.Original] = cached;
                        continue;
                    }

                    next.Add((item.Original, super));
                }

                pending = next;
                if (typesToResolve > 0)
                {
                    ReportProgress(progress, ref lastProgressUtc, false, new OpcUaBrowseProgress
                    {
                        Phase = "prefetch",
                        VariablesFound = typesToResolve,
                        NodesBrowsed = typesToResolve - pending.Count
                    });
                }
            }

            foreach (var item in pending)
            {
                _browseTypeCache[item.Original] = OpcUaDataTypeKind.Structure;
            }

            if (typesToResolve > 0)
            {
                ReportProgress(progress, ref lastProgressUtc, true, new OpcUaBrowseProgress
                {
                    Phase = "prefetch",
                    VariablesFound = typesToResolve,
                    NodesBrowsed = typesToResolve
                });
            }
        }

        private Dictionary<NodeId, NodeId> BrowseSuperTypes(
            OpcUaAuxSession session,
            List<NodeId> typeIds,
            CancellationToken cancellationToken,
            ref int failures)
        {
            var map = new Dictionary<NodeId, NodeId>();
            if (typeIds.Count == 0)
            {
                return map;
            }

            var descriptions = new BrowseDescriptionCollection();
            foreach (var typeId in typeIds)
            {
                descriptions.Add(new BrowseDescription
                {
                    NodeId = typeId,
                    BrowseDirection = BrowseDirection.Inverse,
                    ReferenceTypeId = ReferenceTypeIds.HasSubtype,
                    IncludeSubtypes = false,
                    NodeClassMask = (uint)NodeClass.DataType,
                    ResultMask = (uint)BrowseResultMask.None
                });
            }

            try
            {
                session.Session.Browse(
                    null,
                    null,
                    1,
                    descriptions,
                    out BrowseResultCollection results,
                    out _);

                if (results == null)
                {
                    return map;
                }

                for (var i = 0; i < typeIds.Count && i < results.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var browseResult = results[i];
                    if (browseResult?.ContinuationPoint != null && browseResult.ContinuationPoint.Length > 0)
                    {
                        ReleaseContinuationPoint(session, browseResult.ContinuationPoint);
                    }

                    if (browseResult == null || StatusCode.IsBad(browseResult.StatusCode)
                        || browseResult.References == null || browseResult.References.Count == 0)
                    {
                        continue;
                    }

                    var super = ToSessionNodeId(session, browseResult.References[0].NodeId);
                    if (super != null)
                    {
                        map[typeIds[i]] = super;
                    }
                }

                return map;
            }
            catch (OpcUaBrowseInterruptedException)
            {
                throw;
            }
            catch (Exception ex) when (IsSessionLost(ex, session.Session))
            {
                throw new OpcUaBrowseInterruptedException("The OPC UA browse session was lost.", ex);
            }
            catch (Exception ex) when (IsCapacityError(ex) && typeIds.Count > 1)
            {
                var mid = typeIds.Count / 2;
                var left = BrowseSuperTypes(session, typeIds.GetRange(0, mid), cancellationToken, ref failures);
                var right = BrowseSuperTypes(session, typeIds.GetRange(mid, typeIds.Count - mid), cancellationToken, ref failures);
                foreach (var pair in right)
                {
                    left[pair.Key] = pair.Value;
                }

                return left;
            }
            catch (Exception ex) when (IsCapacityError(ex))
            {
                failures += typeIds.Count;
                return map;
            }
        }

        private void ClassifyRead(
            OpcUaAuxSession session,
            VariableTypeRead read,
            Queue<NodeId> expandables,
            List<OpcUaScalarVariable> scalars,
            List<VariableTypeRead> arrays)
        {
            if (read.Variable?.NodeId == null)
            {
                return;
            }

            if (!read.Ok)
            {
                expandables.Enqueue(read.Variable.NodeId);
                return;
            }

            if (read.ValueRank >= ValueRanks.OneDimension)
            {
                // An array is one variable (Part 3); its elements become tags through an IndexRange, without a
                // browse of the element nodes some servers add. Arrays of structures keep the browse of their
                // children, the members of a structure are only reachable as nodes.
                var arrayKind = ResolveDataTypeKind(read.DataTypeId);
                if (arrayKind == OpcUaDataTypeKind.Structure)
                {
                    expandables.Enqueue(read.Variable.NodeId);
                }
                else if (arrayKind != OpcUaDataTypeKind.Rejected && read.Variable.NodeId.IdType == IdType.String)
                {
                    read.Kind = arrayKind;
                    arrays.Add(read);
                }

                return;
            }

            if (read.ValueRank != ValueRanks.Scalar)
            {
                expandables.Enqueue(read.Variable.NodeId);
                return;
            }

            var kind = ResolveDataTypeKind(read.DataTypeId);
            if (kind == OpcUaDataTypeKind.Structure)
            {
                expandables.Enqueue(read.Variable.NodeId);
                return;
            }

            // Text and object types (String, LocalizedText, DateTime, NodeId ...) never give a number: like the S7
            // browse, the browse leaves them out, so a new browse removes the ones it imported as numbers before
            if (kind == OpcUaDataTypeKind.Rejected)
            {
                return;
            }

            if (read.Variable.NodeId.IdType != IdType.String)
            {
                return;
            }

            var address = ToAddress(session, read.Variable.NodeId);
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            scalars.Add(new OpcUaScalarVariable
            {
                Address = address,
                Name = GetTagName(address, read.Variable.DisplayName, read.Variable.BrowseName),
                DisplayName = read.Variable.DisplayName,
                TypeId = kind == OpcUaDataTypeKind.Boolean ? 1 : 2
            });
        }

        /// <summary>
        /// One tag per element of the arrays of numbers and bools, addressed by an IndexRange. The size comes from
        /// ArrayDimensions, or from the current value when the server gives no fixed size. The element names show
        /// the index of the PLC (Array[1..10] starts at [1]) when the server has element nodes named like that.
        /// </summary>
        private void ExpandArrays(
            OpcUaAuxSession session,
            List<VariableTypeRead> arrays,
            List<OpcUaScalarVariable> scalars,
            List<KeyValuePair<string, string>> renames,
            CancellationToken cancellationToken)
        {
            if (arrays.Count == 0)
            {
                return;
            }

            var dimensions = new int[arrays.Count][];
            for (var i = 0; i < arrays.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dimensions[i] = FixedDimensions(arrays[i]) ?? ReadValueDimensions(session, arrays[i].Variable.NodeId);
            }

            var lowerBounds = ReadElementLowerBounds(session, arrays, dimensions, cancellationToken);
            for (var i = 0; i < arrays.Count; i++)
            {
                var dims = dimensions[i];
                if (dims == null)
                {
                    continue;
                }

                var array = arrays[i];
                var address = ToAddress(session, array.Variable.NodeId);
                var name = GetTagName(address, array.Variable.DisplayName, array.Variable.BrowseName);
                var displayName = array.Variable.DisplayName ?? array.Variable.BrowseName ?? name;
                var typeId = array.Kind == OpcUaDataTypeKind.Boolean ? 1 : 2;
                var bounds = lowerBounds[i];
                var truncated = OpcUaArrayElements.ForEach(dims, indexes =>
                {
                    var elementAddress = OpcUaArrayElements.Address(address, indexes);
                    var text = "[" + OpcUaArrayElements.FormatIndexes(indexes, bounds) + "]";
                    scalars.Add(new OpcUaScalarVariable
                    {
                        Address = elementAddress,
                        Name = name + text,
                        DisplayName = displayName + text,
                        TypeId = typeId
                    });
                    if (bounds != null)
                    {
                        // The element node of the server: tags browsed before keep their id and history.
                        renames.Add(new KeyValuePair<string, string>(address + text, elementAddress));
                    }
                });
                if (truncated)
                {
                    LastArraysTruncated++;
                }
            }
        }

        internal static int[] FixedDimensions(int valueRank, uint[] arrayDimensions)
        {
            if (arrayDimensions == null || arrayDimensions.Length != valueRank)
            {
                return null;
            }

            var dims = new int[arrayDimensions.Length];
            for (var d = 0; d < dims.Length; d++)
            {
                if (arrayDimensions[d] == 0)
                {
                    return null;
                }

                dims[d] = (int)Math.Min(arrayDimensions[d], int.MaxValue);
            }

            return dims;
        }

        private static int[] FixedDimensions(VariableTypeRead read) => FixedDimensions(read.ValueRank, read.ArrayDimensions);

        // The size of the current value, for an array without fixed ArrayDimensions.
        private int[] ReadValueDimensions(OpcUaAuxSession session, NodeId nodeId)
        {
            try
            {
                var nodesToRead = new ReadValueIdCollection
                {
                    new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value }
                };
                session.Session.Read(null, 0, TimestampsToReturn.Neither, nodesToRead, out DataValueCollection values, out _);
                var value = values != null && values.Count > 0 ? values[0] : null;
                if (value == null)
                {
                    _arrayFailures++;
                    return null;
                }

                if (IsTransportStatus(value.StatusCode))
                {
                    throw new OpcUaBrowseInterruptedException(
                        "The OPC UA browse session was lost.", new ServiceResultException(value.StatusCode));
                }

                if (StatusCode.IsBad(value.StatusCode))
                {
                    _arrayFailures++;
                    return null;
                }

                return value.Value switch
                {
                    Matrix matrix => matrix.Dimensions,
                    Array array when array.Length > 0 => [array.Length],
                    _ => null
                };
            }
            catch (OpcUaBrowseInterruptedException)
            {
                throw;
            }
            catch (Exception ex) when (IsSessionLost(ex, session.Session))
            {
                throw new OpcUaBrowseInterruptedException("The OPC UA browse session was lost.", ex);
            }
            catch (Exception ex) when (IsCapacityError(ex))
            {
                _arrayFailures++;
                return null;
            }
        }

        /// <summary>
        /// The index of the first element as the server names its element nodes ("x"[1] for Array[1..10]), per
        /// array, null where the server has no such nodes. A name is only taken when the node one before the first
        /// does not exist and the last element does, so a wrong guess never maps tags to other elements.
        /// </summary>
        private int[][] ReadElementLowerBounds(
            OpcUaAuxSession session,
            List<VariableTypeRead> arrays,
            int[][] dimensions,
            CancellationToken cancellationToken)
        {
            var result = new int[arrays.Count][];
            var candidates = new int[arrays.Count][];
            var pending = new List<int>();
            for (var i = 0; i < arrays.Count; i++)
            {
                if (dimensions[i] != null)
                {
                    pending.Add(i);
                }
            }

            // First child of each array; a server that hands out no more continuation points gets single calls.
            var batchSize = Math.Max(1, Math.Min(session.MaxNodesPerBrowse, MaxParentNodesPerBrowseCall));
            var single = new List<int>();
            foreach (var batch in pending.Chunk(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrowseFirstChildren(session, arrays, dimensions, batch, candidates, single);
            }

            foreach (var index in single)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BrowseFirstChildren(session, arrays, dimensions, [index], candidates, null);
            }

            // Check: element "first - 1" in each dimension is unknown, the last element exists.
            var checks = new List<(int Array, NodeId NodeId, bool MustExist)>();
            for (var i = 0; i < arrays.Count; i++)
            {
                var first = candidates[i];
                if (first == null)
                {
                    continue;
                }

                var dims = dimensions[i];
                var last = new int[first.Length];
                for (var d = 0; d < first.Length; d++)
                {
                    last[d] = first[d] + dims[d] - 1;
                    var before = (int[])first.Clone();
                    before[d]--;
                    checks.Add((i, ElementNodeId(arrays[i].Variable.NodeId, before), false));
                }

                checks.Add((i, ElementNodeId(arrays[i].Variable.NodeId, last), true));
            }

            var rejected = new HashSet<int>();
            var readSize = Math.Max(1, session.MaxNodesPerRead);
            for (var offset = 0; offset < checks.Count; offset += readSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var part = checks.GetRange(offset, Math.Min(readSize, checks.Count - offset));
                var exists = ReadNodesExist(session, part.ConvertAll(c => c.NodeId));
                for (var k = 0; k < part.Count; k++)
                {
                    if (exists == null || exists[k] != part[k].MustExist)
                    {
                        rejected.Add(part[k].Array);
                    }
                }
            }

            for (var i = 0; i < arrays.Count; i++)
            {
                if (candidates[i] != null && !rejected.Contains(i))
                {
                    result[i] = candidates[i];
                }
            }

            return result;
        }

        private void BrowseFirstChildren(
            OpcUaAuxSession session,
            List<VariableTypeRead> arrays,
            int[][] dimensions,
            IReadOnlyList<int> batch,
            int[][] candidates,
            List<int> noContinuationPoints)
        {
            var descriptions = new BrowseDescriptionCollection();
            foreach (var i in batch)
            {
                descriptions.Add(new BrowseDescription
                {
                    NodeId = arrays[i].Variable.NodeId,
                    BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                    IncludeSubtypes = true,
                    NodeClassMask = (uint)NodeClass.Variable,
                    ResultMask = (uint)BrowseResultMask.BrowseName
                });
            }

            BrowseResultCollection results;
            try
            {
                session.Session.Browse(null, null, 1, descriptions, out results, out _);
            }
            catch (Exception ex) when (IsSessionLost(ex, session.Session))
            {
                throw new OpcUaBrowseInterruptedException("The OPC UA browse session was lost.", ex);
            }
            catch (Exception ex) when (IsCapacityError(ex))
            {
                if (noContinuationPoints != null)
                {
                    noContinuationPoints.AddRange(batch);
                }

                return;
            }

            for (var k = 0; k < batch.Count && results != null && k < results.Count; k++)
            {
                var result = results[k];
                if (result?.ContinuationPoint != null && result.ContinuationPoint.Length > 0)
                {
                    ReleaseContinuationPoint(session, result.ContinuationPoint);
                }

                if (result == null)
                {
                    continue;
                }

                if (IsTransportStatus(result.StatusCode))
                {
                    throw new OpcUaBrowseInterruptedException(
                        "The OPC UA browse session was lost.", new ServiceResultException(result.StatusCode));
                }

                if (result.StatusCode == StatusCodes.BadNoContinuationPoints)
                {
                    noContinuationPoints?.Add(batch[k]);
                    continue;
                }

                if (StatusCode.IsBad(result.StatusCode) || result.References == null || result.References.Count == 0)
                {
                    continue;
                }

                var i = batch[k];
                var child = ToSessionNodeId(session, result.References[0].NodeId);
                var first = ParseElementIndexes(result.References[0].BrowseName?.Name, dimensions[i].Length);
                if (first != null && child != null && child.Equals(ElementNodeId(arrays[i].Variable.NodeId, first)))
                {
                    candidates[i] = first;
                }
            }
        }

        internal static int[] ParseElementIndexes(string browseName, int rank) =>
            OpcUaArrayElements.ParseIndexes(browseName, rank);

        // The element node as servers with element nodes name it: the identifier of the array with "[i,j]".
        internal static NodeId ElementNodeId(NodeId arrayNodeId, IReadOnlyList<int> indexes)
        {
            return new NodeId(
                OpcUaArrayElements.ElementIdentifier(arrayNodeId.Identifier as string, indexes), arrayNodeId.NamespaceIndex);
        }

        // Per node whether it exists (its NodeClass can be read), null when the read failed as a whole.
        private bool[] ReadNodesExist(OpcUaAuxSession session, List<NodeId> nodeIds)
        {
            try
            {
                var nodesToRead = new ReadValueIdCollection();
                foreach (var nodeId in nodeIds)
                {
                    nodesToRead.Add(new ReadValueId { NodeId = nodeId, AttributeId = Attributes.NodeClass });
                }

                session.Session.Read(null, 0, TimestampsToReturn.Neither, nodesToRead, out DataValueCollection values, out _);
                if (values == null || values.Count < nodeIds.Count)
                {
                    return null;
                }

                var exists = new bool[nodeIds.Count];
                for (var k = 0; k < nodeIds.Count; k++)
                {
                    if (IsTransportStatus(values[k].StatusCode))
                    {
                        throw new OpcUaBrowseInterruptedException(
                            "The OPC UA browse session was lost.", new ServiceResultException(values[k].StatusCode));
                    }

                    exists[k] = StatusCode.IsGood(values[k].StatusCode);
                }

                return exists;
            }
            catch (OpcUaBrowseInterruptedException)
            {
                throw;
            }
            catch (Exception ex) when (IsSessionLost(ex, session.Session))
            {
                throw new OpcUaBrowseInterruptedException("The OPC UA browse session was lost.", ex);
            }
            catch (Exception ex) when (IsCapacityError(ex))
            {
                return null;
            }
        }

        private OpcUaDataTypeKind ResolveDataTypeKind(NodeId dataTypeId)
        {
            _browseTypeCache ??= new Dictionary<NodeId, OpcUaDataTypeKind>();
            if (_browseTypeCache.TryGetValue(dataTypeId, out var cached))
            {
                return cached;
            }

            var kind = ClassifyKnownDataType(dataTypeId) ?? OpcUaDataTypeKind.Structure;
            _browseTypeCache[dataTypeId] = kind;
            return kind;
        }

        internal static OpcUaDataTypeKind? ClassifyKnownDataType(NodeId dataTypeId)
        {
            var mapped = MapBuiltinType(dataTypeId);
            if (mapped == 1)
            {
                return OpcUaDataTypeKind.Boolean;
            }

            if (mapped == 2)
            {
                return OpcUaDataTypeKind.Number;
            }

            if (IsStructureDataType(dataTypeId))
            {
                return OpcUaDataTypeKind.Structure;
            }

            if (IsRejectedDataType(dataTypeId))
            {
                return OpcUaDataTypeKind.Rejected;
            }

            return null;
        }

        private static List<List<NodeId>> SplitList(List<NodeId> items, int chunkSize)
        {
            var result = new List<List<NodeId>>();
            for (var offset = 0; offset < items.Count; offset += chunkSize)
            {
                result.Add(items.GetRange(offset, Math.Min(chunkSize, items.Count - offset)));
            }

            return result;
        }

        private static bool IsSessionLost(Exception ex, Session session)
        {
            _ = session;
            if (ex == null || ex is OperationCanceledException || ex is OpcUaBrowseInterruptedException)
            {
                return false;
            }

            if (ex is TimeoutException)
            {
                return true;
            }

            if (ex is ServiceResultException sre && IsTransportStatus(sre.StatusCode))
            {
                return true;
            }

            return IsSessionLost(ex.InnerException, session);
        }

        private static bool IsTransportStatus(StatusCode statusCode)
        {
            var code = (uint)statusCode;
            return code == StatusCodes.BadTimeout
                || code == StatusCodes.BadRequestTimeout
                || code == StatusCodes.BadSecureChannelClosed
                || code == StatusCodes.BadConnectionClosed
                || code == StatusCodes.BadSessionClosed
                || code == StatusCodes.BadDisconnect
                || code == StatusCodes.BadNotConnected
                || code == StatusCodes.BadServerNotConnected
                || code == StatusCodes.BadNoCommunication
                || code == StatusCodes.BadRequestInterrupted
                || code == StatusCodes.BadSecureChannelIdInvalid;
        }

        private static bool IsCapacityError(Exception ex)
        {
            if (ex is ServiceResultException sre)
            {
                return IsCapacityStatus(sre.StatusCode);
            }

            return ex.InnerException != null && IsCapacityError(ex.InnerException);
        }

        private static bool IsCapacityStatus(StatusCode statusCode)
        {
            var code = (uint)statusCode;
            return code == StatusCodes.BadTooManyOperations
                || code == StatusCodes.BadRequestTooLarge
                || code == StatusCodes.BadResponseTooLarge
                || code == StatusCodes.BadTcpMessageTooLarge
                || code == StatusCodes.BadEncodingLimitsExceeded
                || code == StatusCodes.BadOutOfMemory
                || code == StatusCodes.BadNoContinuationPoints;
        }

        private void ReleaseContinuationPoints(OpcUaAuxSession session, ByteStringCollection continuationPoints)
        {
            if (continuationPoints == null || continuationPoints.Count == 0)
            {
                return;
            }

            foreach (var continuationPoint in continuationPoints)
            {
                ReleaseContinuationPoint(session, continuationPoint);
            }
        }

        private void ReleaseContinuationPoint(OpcUaAuxSession session, byte[] continuationPoint)
        {
            if (continuationPoint == null || continuationPoint.Length == 0)
            {
                return;
            }

            try
            {
                session.Session.BrowseNext(
                    null,
                    true,
                    new ByteStringCollection { continuationPoint },
                    out _,
                    out _);
            }
            catch
            {
                // Releasing a continuation point is best-effort.
            }
        }

        private static int? MapBuiltinType(NodeId dataTypeId)
        {
            if (dataTypeId == null || dataTypeId.NamespaceIndex != 0 || dataTypeId.IdType != IdType.Numeric)
            {
                return null;
            }

            var identifier = Convert.ToUInt32(dataTypeId.Identifier);
            if (identifier == (uint)BuiltInType.Boolean)
            {
                return 1;
            }

            if (identifier >= (uint)BuiltInType.SByte && identifier <= (uint)BuiltInType.Double)
            {
                return 2;
            }

            // An enumeration is an Int32; BaseDataType (Variant) can hold a number, the value decides
            if (identifier == (uint)BuiltInType.Number
                || identifier == (uint)BuiltInType.Integer
                || identifier == (uint)BuiltInType.UInteger
                || identifier == (uint)BuiltInType.Enumeration
                || identifier == (uint)BuiltInType.Variant)
            {
                return 2;
            }

            return null;
        }

        private static bool IsStructureDataType(NodeId dataTypeId)
        {
            return dataTypeId == DataTypeIds.Structure || dataTypeId == DataTypeIds.Union;
        }

        private static bool IsRejectedDataType(NodeId dataTypeId)
        {
            if (dataTypeId == null)
            {
                return false;
            }

            if (dataTypeId.NamespaceIndex != 0 || dataTypeId.IdType != IdType.Numeric)
            {
                return false;
            }

            var identifier = Convert.ToUInt32(dataTypeId.Identifier);
            return identifier == (uint)BuiltInType.String
                || identifier == (uint)BuiltInType.DateTime
                || identifier == (uint)BuiltInType.Guid
                || identifier == (uint)BuiltInType.ByteString
                || identifier == (uint)BuiltInType.XmlElement
                || identifier == (uint)BuiltInType.NodeId
                || identifier == (uint)BuiltInType.ExpandedNodeId
                || identifier == (uint)BuiltInType.StatusCode
                || identifier == (uint)BuiltInType.QualifiedName
                || identifier == (uint)BuiltInType.LocalizedText
                || identifier == (uint)BuiltInType.ExtensionObject
                || identifier == (uint)BuiltInType.DataValue
                || identifier == (uint)BuiltInType.DiagnosticInfo;
        }

        private static bool TryGetValueRank(DataValue value, out int valueRank)
        {
            valueRank = ValueRanks.Any;
            if (value == null || StatusCode.IsBad(value.StatusCode) || value.Value == null)
            {
                return false;
            }

            try
            {
                valueRank = Convert.ToInt32(value.Value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // An S7-1500 shows each Program_Alarm instance as an object with SIG and TIMESTAMP. TIA leaves these instances
        // out of its OPC UA XML export (the instances of other system FBs such as R_TRIG are in it), and the alarm
        // itself is read as an alarm. The browse leaves them out too, so it gives the same tags as the XML import.
        private const string SiemensNamespaceUri = "http://www.siemens.com/simatic-s7-opcua";
        private const string ProgramAlarmTypeId = "OT_Program_Alarm";

        internal static bool IsProgramAlarmInstance(ExpandedNodeId typeDefinition, NamespaceTable namespaces)
        {
            if (typeDefinition == null || !string.Equals(typeDefinition.Identifier as string, ProgramAlarmTypeId, StringComparison.Ordinal))
            {
                return false;
            }

            var namespaceUri = typeDefinition.NamespaceUri ?? namespaces?.GetString(typeDefinition.NamespaceIndex);
            return string.Equals(namespaceUri, SiemensNamespaceUri, StringComparison.Ordinal);
        }

        private static bool IsSkippedObject(NodeId nodeId)
        {
            return nodeId == ObjectIds.Server
                || nodeId == ObjectIds.TypesFolder
                || nodeId == ObjectIds.ViewsFolder
                || nodeId == ObjectIds.RootFolder;
        }

        private static NodeId ToSessionNodeId(OpcUaAuxSession session, ExpandedNodeId expandedNodeId)
        {
            if (expandedNodeId == null)
            {
                return null;
            }

            try
            {
                return ExpandedNodeId.ToNodeId(expandedNodeId, session.Session.NamespaceUris);
            }
            catch
            {
                return null;
            }
        }

        private static string ToAddress(OpcUaAuxSession session, NodeId nodeId)
        {
            string namespaceUri = null;
            if (nodeId.NamespaceIndex >= 0 && nodeId.NamespaceIndex < session.Namespaces.Count)
            {
                namespaceUri = session.Namespaces[nodeId.NamespaceIndex];
            }
            else if (session.Session != null)
            {
                namespaceUri = session.Session.NamespaceUris.GetString(nodeId.NamespaceIndex);
            }

            return new ExpandedNodeId(nodeId, namespaceUri).ToString();
        }

        private static string GetTagName(string address, string displayName, string browseName)
        {
            var marker = ";s=";
            var index = address.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && index + marker.Length < address.Length)
            {
                var identifier = address[(index + marker.Length)..];
                if (identifier.Contains('"'))
                {
                    identifier = identifier.Replace("\"", "");
                }

                if (!string.IsNullOrWhiteSpace(identifier))
                {
                    return identifier;
                }
            }

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            if (!string.IsNullOrWhiteSpace(browseName))
            {
                return browseName;
            }

            return address;
        }

        private void ReportWalk(OpcUaAuxSession session, string detail)
        {
            if (_walkProgress == null || !ReferenceEquals(session, _walkSession))
            {
                return;
            }

            ReportProgress(_walkProgress, ref _walkProgressUtc, false, new OpcUaBrowseProgress
            {
                Phase = "browse",
                NodesBrowsed = _walkNodes,
                VariablesFound = _walkTags,
                ScalarTags = _walkTags,
                Detail = detail
            });
        }

        private static void ReportProgress(
            Action<OpcUaBrowseProgress> progress,
            ref DateTime lastProgressUtc,
            bool force,
            OpcUaBrowseProgress value)
        {
            if (progress == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (!force && (now - lastProgressUtc).TotalMilliseconds < 1000)
            {
                return;
            }

            lastProgressUtc = now;
            progress(value);
        }

        private sealed class VariableTypeRead
        {
            public OpcUaVariableNode Variable { get; set; }
            public NodeId DataTypeId { get; set; }
            public int ValueRank { get; set; }
            public uint[] ArrayDimensions { get; set; }
            public OpcUaDataTypeKind Kind { get; set; }
            public bool Ok { get; set; }
        }
        #endregion

        #region event handling
        public event EventHandler<BrowseResultItemEventArgs> OnBrowseItem;
        private void DoOnBrowseItem(BrowseResultItem item)
        {
            OnBrowseItem?.Invoke(this, new BrowseResultItemEventArgs(item));
        }
        #endregion
    }

    public class OpcUaClientApi : OpcUaBrowser
    {
        #region member
        private readonly object _monitoredItemLock = new();
        private readonly Dictionary<int, ApiSubscription> _apiSubscriptionDictionary = [];
        private readonly Dictionary<string, ApiSubscription> _expNodeIdToSubscriptionDictionary = [];
        private readonly object _alarmSubscriptionLock = new();
        private AlarmEventSubscription _alarmEventSubscription;
        private bool _alarmEventsRequested;
        #endregion

        #region construction
        public OpcUaClientApi()
        {
            OnStateChange += OpcUaClientApi_OnStateChange;
        }
        #endregion

        #region state change
        private void OpcUaClientApi_OnStateChange(object sender, StateChangeEventArgs e)
        {
            if (e.State == ConnectionState.Connected)
            {
                lock (_monitoredItemLock)
                {
                    if (_apiSubscriptionDictionary.Count > 0)
                    {
                        DoOnClientMessage("Re-add monitored items on reconnect ...");
                    }
                    foreach (var s in _apiSubscriptionDictionary)
                    {
                        s.Value.OnSessionReconnect(_serverSession);
                    }
                }
            }

            if (e.State == ConnectionState.Disconnected)
            {
                lock (_monitoredItemLock)
                {
                    _apiSubscriptionDictionary.Clear();
                    _expNodeIdToSubscriptionDictionary.Clear();
                }

                lock (_alarmSubscriptionLock)
                {
                    _alarmStartGeneration++;
                    _alarmEventSubscription?.Dispose();
                    _alarmEventSubscription = null;
                    _alarmPresentation = AlarmPresentation.Empty;
                    ClearAlarmNames();
                }

                ResetBrowseCache();
            }
        }
        #endregion

        #region alarm events
        public event Action AlarmSubscriptionPreparing;
        public event Action PreparingAlarmSubscription;
        // The server refused ConditionRefresh. No RefreshStart or RefreshEnd follows.
        public event Action AlarmRefreshRejected;
        public event EventHandler<OpcUaAlarmNotificationEventArgs> OnAlarmNotification;

        private int _alarmStartGeneration;
        private AlarmPresentation _alarmPresentation = AlarmPresentation.Empty;
        private readonly object _alarmNameLock = new();
        private int _alarmNameGeneration;
        private readonly Dictionary<string, string> _alarmClassNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _alarmClassNamePending = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _conditionClassNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _conditionClassPending = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _eventTypeNameCache = new(StringComparer.Ordinal);
        private readonly HashSet<string> _eventTypeNamePending = new(StringComparer.Ordinal);

        private const int MaxConditionTypes = 64;
        private const int MaxConditionTypeDepth = 6;
        private const int MaxAlarmClassNames = 32;
        private const int MaxConditionClassNames = 64;
        private const int MaxAlarmConditionRefs = 200;
        private const string AlarmConditionsFolderName = "Alarm conditions";
        private const string AlarmClassPropertyName = "AlarmClass";
        private const int MaxAlarmPropertyClauses = 8;
        private const int MaxContinuationRounds = 2;
        private const uint AlarmTypeBrowseLimit = 64;
        private static readonly string[] AlarmClassPropertyNames =
        [
            "ALARMCLASSSYMBOL",
            "TEXTCOLOR",
            "BACKCOLOR",
            "FLASHING",
            "ALARMCLASS"
        ];

        public bool TryRefreshAlarms(Func<bool> tryArm, Action disarm)
        {
            lock (_alarmSubscriptionLock)
            {
                if (_alarmEventSubscription == null)
                {
                    DoOnClientMessage("Alarm refresh skipped, client is not subscribed.");
                    return false;
                }

                if (tryArm == null || !tryArm())
                {
                    return false;
                }

                try
                {
                    if (!_alarmEventSubscription.Refresh())
                    {
                        AlarmRefreshRejected?.Invoke();
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    disarm?.Invoke();
                    DoOnClientMessage($"Condition refresh failed: {ex.Message}");
                    return false;
                }
            }
        }

        public void SetAlarmEventsEnabled(bool enabled)
        {
            int generation;
            lock (_alarmSubscriptionLock)
            {
                _alarmEventsRequested = enabled;
                _alarmStartGeneration++;
                generation = _alarmStartGeneration;
                _alarmEventSubscription?.Dispose();
                _alarmEventSubscription = null;
                if (!enabled || !IsConnected)
                {
                    return;
                }
            }

            // The archive seed has to finish before the first notification, but it must not hold
            // the connect callback. A newer generation makes a pending start give up.
            new Thread(() => StartAlarmSubscription(generation))
            {
                IsBackground = true,
                Name = "AlarmSubscriptionStart"
            }.Start();
        }

        private void StartAlarmSubscription(int generation)
        {
            if (!IsAlarmStartCurrent(generation))
            {
                return;
            }

            try
            {
                AlarmSubscriptionPreparing?.Invoke();
            }
            catch (Exception ex)
            {
                DoOnClientMessage($"Alarm archive seed failed: {ex.Message}");
            }

            lock (_alarmSubscriptionLock)
            {
                if (!IsAlarmStartCurrent(generation) || _serverSession == null)
                {
                    return;
                }

                try
                {
                    PreparingAlarmSubscription?.Invoke();
                    _alarmEventSubscription = new AlarmEventSubscription(this, _serverSession, Notification_AlarmEvent);
                    if (!_alarmEventSubscription.Start())
                    {
                        AlarmRefreshRejected?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    _alarmEventSubscription?.Dispose();
                    _alarmEventSubscription = null;
                    DoOnClientError($"Alarm subscription start failed: {ex.Message}");
                }
            }
        }

        private bool IsAlarmStartCurrent(int generation)
        {
            lock (_alarmSubscriptionLock)
            {
                return generation == _alarmStartGeneration && _alarmEventsRequested && IsConnected;
            }
        }

        private void Notification_AlarmEvent(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {
            if (e?.NotificationValue is not EventFieldList fields)
            {
                return;
            }

            var notification = ParseAlarmNotification(fields);
            if (notification == null)
            {
                return;
            }

            OnAlarmNotification?.Invoke(this, new OpcUaAlarmNotificationEventArgs(notification));
        }

        private const int AlarmFieldCount = 22;
        private const int AlarmFieldEventType = 1;
        private const int AlarmFieldTime = 2;
        private const int AlarmFieldMessage = 3;
        private const int AlarmFieldSeverity = 4;
        private const int AlarmFieldConditionName = 5;
        private const int AlarmFieldConditionId = 6;
        private const int AlarmFieldBranchId = 7;
        private const int AlarmFieldRetain = 8;
        private const int AlarmFieldEnabledId = 9;
        private const int AlarmFieldActiveId = 10;
        private const int AlarmFieldActiveSince = 11;
        private const int AlarmFieldAckedId = 12;
        private const int AlarmFieldConfirmedId = 13;
        private const int AlarmFieldSourceName = 14;
        private const int AlarmFieldActiveValue = 15;
        private const int AlarmFieldAckedValue = 16;
        private const int AlarmFieldSuppressedId = 17;
        private const int AlarmFieldOutOfServiceId = 18;
        private const int AlarmFieldSuppressedOrShelved = 19;
        private const int AlarmFieldLimitState = 20;
        private const int AlarmFieldReceiveTime = 21;
        // Condition instance. Used only when the server has neither ConditionId nor ConditionName.
        private const int AlarmFieldConditionNodeId = 22;

        private static readonly string[] ActiveFalseTokens = ["inactive", "inaktiv", "not active", "nicht aktiv"];
        private static readonly string[] ActiveTrueTokens = ["active", "aktiv"];
        private static readonly string[] AckFalseTokens = ["unacknowledged", "unacked", "not acknowledged", "not acked", "unquittiert", "nicht quittiert"];
        private static readonly string[] AckTrueTokens = ["acknowledged", "acked", "quittiert"];
        private static readonly string[] EnabledFalseTokens = ["disabled", "deaktiviert"];
        private static readonly string[] EnabledTrueTokens = ["enabled", "aktiviert"];
        private static readonly string[] ConfirmedFalseTokens = ["unconfirmed", "not confirmed", "unbestätigt", "nicht bestätigt"];
        private static readonly string[] ConfirmedTrueTokens = ["confirmed", "bestätigt"];
        private static readonly string[] SuppressedFalseTokens = ["unsuppressed", "not suppressed", "nicht unterdrückt"];
        private static readonly string[] SuppressedTrueTokens = ["suppressed", "unterdrückt"];
        private static readonly string[] OutOfServiceFalseTokens = ["not out of service", "in service", "nicht außer betrieb"];
        private static readonly string[] OutOfServiceTrueTokens = ["out of service", "outofservice", "außer betrieb"];

        private OpcUaAlarmNotification ParseAlarmNotification(EventFieldList fields)
        {
            if (fields?.EventFields == null || fields.EventFields.Count <= AlarmFieldEventType)
            {
                return null;
            }

            var eventType = ParseNodeIdFromVariant(fields.EventFields[AlarmFieldEventType]);
            var noticeKind = NoticeKind(eventType);
            if (noticeKind != OpcUaAlarmNoticeKind.Alarm)
            {
                var noticeTime = fields.EventFields.Count > AlarmFieldTime
                    ? ParseEventTime(fields.EventFields[AlarmFieldTime])
                    : DateTime.UtcNow;
                return new OpcUaAlarmNotification
                {
                    Kind = noticeKind,
                    EventType = eventType,
                    Time = noticeTime
                };
            }

            if (fields.EventFields.Count < AlarmFieldCount)
            {
                return null;
            }

            var observedUtc = DateTime.UtcNow;
            var time = ParseEventTime(fields.EventFields[AlarmFieldTime]);
            var receiveTime = ParseEventTimeOrMin(fields.EventFields[AlarmFieldReceiveTime]);
            var message = VariantText(fields.EventFields[AlarmFieldMessage]);
            var severity = ParseSeverity(fields.EventFields[AlarmFieldSeverity]);
            var conditionName = VariantText(fields.EventFields[AlarmFieldConditionName]);
            var conditionId = ConditionIdForAlarm(
                ParseNodeIdFromVariant(fields.EventFields[AlarmFieldConditionId]),
                conditionName,
                ParseConditionInstanceId(fields));
            var branchId = ParseNodeIdFromVariant(fields.EventFields[AlarmFieldBranchId]);
            var retain = ParseBoolOrNull(fields.EventFields[AlarmFieldRetain]);
            var enabled = ParseStateFlag(
                fields.EventFields[AlarmFieldEnabledId],
                Variant.Null,
                EnabledFalseTokens,
                EnabledTrueTokens);
            var active = ParseStateFlag(
                fields.EventFields[AlarmFieldActiveId],
                fields.EventFields[AlarmFieldActiveValue],
                ActiveFalseTokens,
                ActiveTrueTokens);
            var activeSince = ParseEventTimeOrMin(fields.EventFields[AlarmFieldActiveSince]);
            var acked = ParseStateFlag(
                fields.EventFields[AlarmFieldAckedId],
                fields.EventFields[AlarmFieldAckedValue],
                AckFalseTokens,
                AckTrueTokens);
            var confirmed = ParseStateFlag(
                fields.EventFields[AlarmFieldConfirmedId],
                Variant.Null,
                ConfirmedFalseTokens,
                ConfirmedTrueTokens);
            var sourceName = VariantText(fields.EventFields[AlarmFieldSourceName]);
            var suppressed = ParseStateFlag(
                fields.EventFields[AlarmFieldSuppressedId],
                Variant.Null,
                SuppressedFalseTokens,
                SuppressedTrueTokens);
            var outOfService = ParseStateFlag(
                fields.EventFields[AlarmFieldOutOfServiceId],
                Variant.Null,
                OutOfServiceFalseTokens,
                OutOfServiceTrueTokens);
            var suppressedOrShelved = ParseBoolOrNull(fields.EventFields[AlarmFieldSuppressedOrShelved]);
            var limitState = VariantText(fields.EventFields[AlarmFieldLimitState]);

            if (conditionId == null && string.IsNullOrWhiteSpace(conditionName))
            {
                return null;
            }

            var notification = new OpcUaAlarmNotification
            {
                EventId = ParseEventId(fields.EventFields[0]),
                EventType = eventType,
                Time = time,
                ReceiveTime = receiveTime,
                ObservedUtc = observedUtc,
                Message = message,
                Severity = severity,
                ConditionName = conditionName,
                ConditionId = conditionId,
                BranchId = branchId,
                Retain = retain,
                Active = active,
                ActiveSince = activeSince,
                Acked = acked,
                Enabled = enabled,
                Confirmed = confirmed,
                Suppressed = suppressed,
                OutOfService = outOfService,
                SuppressedOrShelved = suppressedOrShelved,
                LimitState = limitState,
                SourceName = sourceName
            };
            ApplyAlarmPresentation(notification, fields, eventType);
            return notification;
        }

        private void ApplyAlarmPresentation(OpcUaAlarmNotification notification, EventFieldList fields, NodeId eventType)
        {
            var presentation = _alarmPresentation ?? AlarmPresentation.Empty;
            notification.EventTypeName = LookupEventTypeName(eventType);
            notification.AlarmClassSymbol = FirstOptionalText(fields, presentation.SymbolIndexes);
            notification.TextColor = FirstColor(fields, presentation.TextColorIndexes);
            notification.BackColor = FirstColor(fields, presentation.BackColorIndexes);
            notification.Flashing = FirstOptionalBool(fields, presentation.FlashingIndexes);
            notification.AlarmClassId = FirstOptionalNode(fields, presentation.ClassIndexes);
            // WinCC Unified sends AlarmClass as a string. Other servers send a NodeId.
            notification.AlarmClassName = notification.AlarmClassId != null
                ? LookupAlarmClassName(notification.AlarmClassId)
                : FirstOptionalText(fields, presentation.ClassIndexes);
        }

        private string FirstOptionalText(EventFieldList fields, int[] indexes)
        {
            if (indexes == null || fields?.EventFields == null)
            {
                return "";
            }

            foreach (var index in indexes)
            {
                if (index < 0 || index >= fields.EventFields.Count)
                {
                    continue;
                }

                var text = OptionalText(fields.EventFields[index]);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }

            return "";
        }

        private static string OptionalText(Variant variant)
        {
            if (variant.Value == null || variant.Value is StatusCode)
            {
                return "";
            }

            return VariantText(variant);
        }

        private static uint? FirstColor(EventFieldList fields, int[] indexes)
        {
            if (indexes == null || fields?.EventFields == null)
            {
                return null;
            }

            foreach (var index in indexes)
            {
                if (index < 0 || index >= fields.EventFields.Count)
                {
                    continue;
                }

                var color = ParseColor(fields.EventFields[index]);
                if (color.HasValue)
                {
                    return color;
                }
            }

            return null;
        }

        private static uint? ParseColor(Variant variant)
        {
            var value = variant.Value;
            if (value == null || value is StatusCode)
            {
                return null;
            }

            return value switch
            {
                uint number => number,
                int number when number >= 0 => (uint)number,
                long number when number >= 0 && number <= uint.MaxValue => (uint)number,
                ushort number => number,
                short number when number >= 0 => (uint)number,
                byte number => number,
                _ => uint.TryParse(value.ToString(), out var parsed) ? parsed : null
            };
        }

        private bool? FirstOptionalBool(EventFieldList fields, int[] indexes)
        {
            if (indexes == null || fields?.EventFields == null)
            {
                return null;
            }

            foreach (var index in indexes)
            {
                if (index < 0 || index >= fields.EventFields.Count)
                {
                    continue;
                }

                var flag = ParseBoolOrNull(fields.EventFields[index]);
                if (flag.HasValue)
                {
                    return flag;
                }
            }

            return null;
        }

        private NodeId FirstOptionalNode(EventFieldList fields, int[] indexes)
        {
            if (indexes == null || fields?.EventFields == null)
            {
                return null;
            }

            foreach (var index in indexes)
            {
                if (index < 0 || index >= fields.EventFields.Count)
                {
                    continue;
                }

                var nodeId = ParseNodeIdFromVariant(fields.EventFields[index]);
                if (nodeId != null)
                {
                    return nodeId;
                }
            }

            return null;
        }

        internal string LookupEventTypeName(NodeId eventType)
        {
            if (eventType == null)
            {
                return "";
            }

            var key = eventType.ToString();
            var presentation = _alarmPresentation ?? AlarmPresentation.Empty;
            if (presentation.EventTypeNames.TryGetValue(key, out var known) && !string.IsNullOrEmpty(known))
            {
                return known;
            }

            return CachedName(key, _eventTypeNameCache, _eventTypeNamePending, eventType, Attributes.BrowseName);
        }

        internal string LookupAlarmClassName(NodeId classId)
        {
            if (classId == null)
            {
                return "";
            }

            return CachedName(classId.ToString(), _alarmClassNames, _alarmClassNamePending, classId, Attributes.DisplayName);
        }

        // WinCC leaves AlarmClass empty on some events. The condition node still has it.
        internal string LookupConditionClassName(string conditionName)
        {
            var key = conditionName?.Trim() ?? "";
            if (key.Length == 0)
            {
                return "";
            }

            int generation;
            Session session;
            lock (_alarmNameLock)
            {
                if (_conditionClassNames.TryGetValue(key, out var name))
                {
                    return name ?? "";
                }

                if (_conditionClassNames.Count >= MaxConditionClassNames || !_conditionClassPending.Add(key))
                {
                    return "";
                }

                generation = _alarmNameGeneration;
                session = _serverSession;
            }

            ThreadPool.QueueUserWorkItem(_ => ReadConditionClassName(session, key, generation));
            return "";
        }

        private void ReadConditionClassName(Session session, string conditionName, int generation)
        {
            var className = "";
            try
            {
                className = ReadConditionClassValue(session, conditionName);
            }
            catch
            {
                // A failed class read stays empty so alarms keep flowing.
            }

            lock (_alarmNameLock)
            {
                _conditionClassPending.Remove(conditionName);
                if (generation != _alarmNameGeneration || _conditionClassNames.Count >= MaxConditionClassNames)
                {
                    return;
                }

                _conditionClassNames[conditionName] = className ?? "";
            }
        }

        private static string ReadConditionClassValue(Session session, string conditionName)
        {
            if (session == null || !session.Connected)
            {
                return "";
            }

            var folderId = FindAlarmConditionsFolder(session);
            if (folderId == null)
            {
                return "";
            }

            var conditionId = FindNamedNode(
                session,
                folderId,
                (uint)NodeClass.Object,
                name => ConditionBrowseNameMatches(name, conditionName));
            if (conditionId == null)
            {
                return "";
            }

            var classId = FindNamedNode(
                session,
                conditionId,
                (uint)NodeClass.Variable,
                name => string.Equals(name, AlarmClassPropertyName, StringComparison.OrdinalIgnoreCase));
            return ReadNodeText(session, classId);
        }

        private static NodeId FindAlarmConditionsFolder(Session session)
        {
            var level = new List<NodeId> { ObjectIds.ObjectsFolder };
            for (var depth = 0; depth < 4 && level.Count > 0; depth++)
            {
                var next = new List<NodeId>();
                foreach (var nodeId in level)
                {
                    foreach (var child in BrowseNamedChildren(session, nodeId, (uint)NodeClass.Object))
                    {
                        if (string.Equals(child.Name, AlarmConditionsFolderName, StringComparison.Ordinal))
                        {
                            return child.NodeId;
                        }

                        if (child.NodeId != ObjectIds.Server && next.Count < 8)
                        {
                            next.Add(child.NodeId);
                        }
                    }
                }

                level = next;
            }

            return null;
        }

        private static NodeId FindNamedNode(Session session, NodeId parentId, uint nodeClassMask, Func<string, bool> match)
        {
            foreach (var child in BrowseNamedChildren(session, parentId, nodeClassMask))
            {
                if (match(child.Name))
                {
                    return child.NodeId;
                }
            }

            return null;
        }

        private static bool ConditionBrowseNameMatches(string browseName, string conditionName)
        {
            if (string.IsNullOrEmpty(browseName) || string.IsNullOrEmpty(conditionName))
            {
                return false;
            }

            if (string.Equals(browseName, conditionName, StringComparison.Ordinal))
            {
                return true;
            }

            return browseName.EndsWith(":" + conditionName, StringComparison.Ordinal)
                || browseName.Contains("::" + conditionName + ":", StringComparison.Ordinal);
        }

        private readonly struct NamedNode
        {
            public NamedNode(string name, NodeId nodeId)
            {
                Name = name;
                NodeId = nodeId;
            }

            public string Name { get; }
            public NodeId NodeId { get; }
        }

        private static List<NamedNode> BrowseNamedChildren(Session session, NodeId nodeId, uint nodeClassMask)
        {
            var children = new List<NamedNode>();
            if (session == null || nodeId == null || !session.Connected)
            {
                return children;
            }

            var description = new BrowseDescription
            {
                NodeId = nodeId,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = nodeClassMask,
                ResultMask = (uint)BrowseResultMask.BrowseName
            };
            session.Browse(
                null,
                null,
                MaxAlarmConditionRefs,
                new BrowseDescriptionCollection { description },
                out BrowseResultCollection results,
                out _);
            if (results == null || results.Count == 0)
            {
                return children;
            }

            var result = results[0];
            TakeNamedReferences(session, result, children);
            var point = result?.ContinuationPoint;
            if (point != null && point.Length > 0 && children.Count < MaxAlarmConditionRefs)
            {
                session.BrowseNext(null, false, new ByteStringCollection { point }, out BrowseResultCollection more, out _);
                if (more != null && more.Count > 0)
                {
                    TakeNamedReferences(session, more[0], children);
                    point = more[0]?.ContinuationPoint;
                }
            }

            if (point != null && point.Length > 0)
            {
                try
                {
                    session.BrowseNext(null, true, new ByteStringCollection { point }, out _, out _);
                }
                catch
                {
                    // Releasing a continuation point is best-effort.
                }
            }

            return children;
        }

        private static void TakeNamedReferences(Session session, BrowseResult result, List<NamedNode> children)
        {
            if (result?.References == null || StatusCode.IsBad(result.StatusCode))
            {
                return;
            }

            foreach (var reference in result.References)
            {
                if (children.Count >= MaxAlarmConditionRefs)
                {
                    return;
                }

                var name = reference?.BrowseName?.Name;
                var childId = ToSessionNodeId(session, reference?.NodeId);
                if (string.IsNullOrEmpty(name) || childId == null)
                {
                    continue;
                }

                children.Add(new NamedNode(name, childId));
            }
        }

        private static NodeId ToSessionNodeId(Session session, ExpandedNodeId expanded)
        {
            if (expanded == null || session?.NamespaceUris == null)
            {
                return null;
            }

            try
            {
                return ExpandedNodeId.ToNodeId(expanded, session.NamespaceUris);
            }
            catch
            {
                return null;
            }
        }

        private static string ReadNodeText(Session session, NodeId nodeId)
        {
            if (session == null || nodeId == null || !session.Connected)
            {
                return "";
            }

            var nodes = new ReadValueIdCollection
            {
                new ReadValueId { NodeId = nodeId, AttributeId = Attributes.Value }
            };
            session.Read(null, 0, TimestampsToReturn.Neither, nodes, out DataValueCollection values, out _);
            if (values == null || values.Count == 0 || values[0] == null || StatusCode.IsBad(values[0].StatusCode))
            {
                return "";
            }

            return values[0].Value switch
            {
                string text => text.Trim(),
                LocalizedText localized => localized.Text?.Trim() ?? "",
                _ => ""
            };
        }

        // Session.Read on the notification thread waits for a response on that same thread.
        private string CachedName(
            string key,
            Dictionary<string, string> cache,
            HashSet<string> pending,
            NodeId nodeId,
            uint attributeId)
        {
            int generation;
            Session session;
            lock (_alarmNameLock)
            {
                if (cache.TryGetValue(key, out var name))
                {
                    return name ?? "";
                }

                if (cache.Count >= MaxAlarmClassNames || !pending.Add(key))
                {
                    return "";
                }

                generation = _alarmNameGeneration;
                session = _serverSession;
            }

            ThreadPool.QueueUserWorkItem(_ => ReadCachedName(
                session, nodeId, key, attributeId, generation, cache, pending));
            return "";
        }

        private void ReadCachedName(
            Session session,
            NodeId nodeId,
            string key,
            uint attributeId,
            int generation,
            Dictionary<string, string> cache,
            HashSet<string> pending)
        {
            var name = "";
            try
            {
                name = ReadNodeName(session, nodeId, attributeId);
            }
            catch
            {
                // A failed name read stays empty so alarms keep flowing.
            }

            lock (_alarmNameLock)
            {
                pending.Remove(key);
                if (generation != _alarmNameGeneration || cache.Count >= MaxAlarmClassNames)
                {
                    return;
                }

                cache[key] = name ?? "";
            }
        }

        private static string ReadNodeName(Session session, NodeId nodeId, uint attributeId)
        {
            if (session == null || nodeId == null || !session.Connected)
            {
                return "";
            }

            var nodes = new ReadValueIdCollection
            {
                new ReadValueId { NodeId = nodeId, AttributeId = attributeId }
            };
            session.Read(null, 0, TimestampsToReturn.Neither, nodes, out DataValueCollection values, out _);
            if (values == null || values.Count == 0 || values[0] == null || StatusCode.IsBad(values[0].StatusCode))
            {
                return "";
            }

            return values[0].Value switch
            {
                LocalizedText text => text.Text?.Trim() ?? "",
                QualifiedName qualified => qualified.Name?.Trim() ?? "",
                _ => ""
            };
        }

        private void ClearAlarmNames()
        {
            lock (_alarmNameLock)
            {
                _alarmNameGeneration++;
                _alarmClassNames.Clear();
                _alarmClassNamePending.Clear();
                _conditionClassNames.Clear();
                _conditionClassPending.Clear();
                _eventTypeNameCache.Clear();
                _eventTypeNamePending.Clear();
            }
        }

        private static string VariantText(Variant variant)
        {
            if (variant.Value is LocalizedText localizedText)
            {
                return localizedText.Text ?? "";
            }

            return variant.Value?.ToString() ?? "";
        }

        private static bool? ParseStateFlag(Variant idField, Variant textField, string[] falseTokens, string[] trueTokens)
        {
            if (idField.Value != null)
            {
                var fromId = ParseNamedState(idField, falseTokens, trueTokens);
                if (fromId.HasValue)
                {
                    return fromId;
                }
            }

            if (textField.Value != null)
            {
                return ParseNamedState(textField, falseTokens, trueTokens);
            }

            return null;
        }

        private static bool? ParseNamedState(Variant variant, string[] falseTokens, string[] trueTokens)
        {
            var asBool = ParseBoolOrNull(variant);
            if (asBool.HasValue)
            {
                return asBool;
            }

            var text = StateText(variant);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (MatchesToken(text, falseTokens))
            {
                return false;
            }

            if (MatchesToken(text, trueTokens))
            {
                return true;
            }

            return null;
        }

        private static bool? ParseBoolOrNull(Variant variant)
        {
            var value = variant.Value;
            if (value == null || value is LocalizedText)
            {
                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            switch (value)
            {
                case byte number:
                    return number == 0 ? false : number == 1 ? true : null;
                case sbyte number:
                    return number == 0 ? false : number == 1 ? true : null;
                case short number:
                    return number == 0 ? false : number == 1 ? true : null;
                case ushort number:
                    return number == 0 ? false : number == 1 ? true : null;
                case int number:
                    return number == 0 ? false : number == 1 ? true : null;
                case uint number:
                    return number == 0 ? false : number == 1 ? true : null;
                case long number:
                    return number == 0 ? false : number == 1 ? true : null;
            }

            return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }

        private static string StateText(Variant variant)
        {
            if (variant.Value is LocalizedText localizedText)
            {
                return localizedText.Text ?? "";
            }

            if (variant.Value is string text)
            {
                return text;
            }

            return "";
        }

        private static bool MatchesToken(string text, string[] tokens)
        {
            var value = text.Trim();
            foreach (var token in tokens)
            {
                if (value.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (value.Length > token.Length
                    && value.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                    && !char.IsLetter(value[token.Length]))
                {
                    return true;
                }
            }

            return false;
        }

        // WinCC keeps ConditionId, or ConditionName when that property is empty.
        // A Siemens alarm has neither. Its condition instance is the NodeId attribute.
        internal static NodeId ConditionIdForAlarm(NodeId conditionId, string conditionName, NodeId instanceId)
        {
            var identity = UsableNodeId(conditionId);
            if (identity != null || !string.IsNullOrWhiteSpace(conditionName))
            {
                return identity;
            }

            return UsableNodeId(instanceId);
        }

        private NodeId ParseConditionInstanceId(EventFieldList fields)
        {
            if (fields?.EventFields == null || fields.EventFields.Count <= AlarmFieldConditionNodeId)
            {
                return null;
            }

            return ParseNodeIdFromVariant(fields.EventFields[AlarmFieldConditionNodeId]);
        }

        private static NodeId UsableNodeId(NodeId nodeId)
        {
            return nodeId != null && !nodeId.IsNullNodeId ? nodeId : null;
        }

        private NodeId ParseNodeIdFromVariant(Variant variant)
        {
            if (variant.Value is NodeId nodeId)
            {
                return nodeId;
            }

            if (variant.Value is ExpandedNodeId expandedNodeId)
            {
                var namespaceUris = _serverSession?.NamespaceUris;
                if (namespaceUris == null)
                {
                    return null;
                }

                return ExpandedNodeId.ToNodeId(expandedNodeId, namespaceUris);
            }

            return null;
        }

        private static ushort ParseSeverity(Variant variant)
        {
            return variant.Value switch
            {
                ushort ushortValue => ushortValue,
                short shortValue when shortValue >= 0 => (ushort)shortValue,
                int intValue when intValue >= 0 && intValue <= ushort.MaxValue => (ushort)intValue,
                uint uintValue when uintValue <= ushort.MaxValue => (ushort)uintValue,
                byte byteValue => byteValue,
                _ => ushort.TryParse(variant.Value?.ToString(), out var parsed) ? parsed : (ushort)0
            };
        }

        private static byte[] ParseEventId(Variant variant)
        {
            if (variant.Value is byte[] bytes && bytes.Length > 0)
            {
                return bytes;
            }

            return null;
        }

        private static DateTime ParseEventTime(Variant variant)
        {
            if (variant.Value is DateTime dateTime)
            {
                return dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime();
            }

            return DateTime.UtcNow;
        }

        private static DateTime ParseEventTimeOrMin(Variant variant)
        {
            if (variant.Value is DateTime dateTime)
            {
                return dateTime.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
                    : dateTime.ToUniversalTime();
            }

            return DateTime.MinValue;
        }

        private static OpcUaAlarmNoticeKind NoticeKind(NodeId eventType)
        {
            if (eventType == null)
            {
                return OpcUaAlarmNoticeKind.Alarm;
            }

            if (NodeId.Equals(eventType, ObjectTypeIds.RefreshStartEventType))
            {
                return OpcUaAlarmNoticeKind.RefreshStart;
            }

            if (NodeId.Equals(eventType, ObjectTypeIds.RefreshEndEventType))
            {
                return OpcUaAlarmNoticeKind.RefreshEnd;
            }

            if (NodeId.Equals(eventType, ObjectTypeIds.RefreshRequiredEventType))
            {
                return OpcUaAlarmNoticeKind.RefreshRequired;
            }

            if (NodeId.Equals(eventType, ObjectTypeIds.EventQueueOverflowEventType))
            {
                return OpcUaAlarmNoticeKind.QueueOverflow;
            }

            return OpcUaAlarmNoticeKind.Alarm;
        }

        #endregion

        #region monitored item
        public void AddMonitoredItem(MonitoredItemInfo monitoredItemInfo)
        {
            lock (_monitoredItemLock)
            {
                var list = new List<MonitoredItemInfo>() { monitoredItemInfo };
                AddInternalMonitoredItems(list);
            }
        }

        public void AddMonitoredItems(List<MonitoredItemInfo> monitoredItemInfos)
        {
            lock (_monitoredItemLock)
            {
                AddInternalMonitoredItems(monitoredItemInfos);
            }
        }

        public void RemoveMonitoredItem(string expNodeIdString)
        {
            lock (_monitoredItemLock)
            {
                var key = expNodeIdString;
                var list = new List<string>() { expNodeIdString };
                RemoveInternalMonitoredItems(list);
            }
        }

        public void RemoveMonitoredItems(List<string> expNodeIdStrings)
        {
            if (expNodeIdStrings == null || expNodeIdStrings.Count == 0)
            {
                return;
            }

            var chunkSize = Math.Max(1, MaxNodesPerRegisterNodes);
            for (var offset = 0; offset < expNodeIdStrings.Count; offset += chunkSize)
            {
                var chunk = expNodeIdStrings.GetRange(offset, Math.Min(chunkSize, expNodeIdStrings.Count - offset));
                lock (_monitoredItemLock)
                {
                    RemoveInternalMonitoredItems(chunk);
                }

                if (offset + chunkSize < expNodeIdStrings.Count)
                {
                    Thread.Sleep(200);
                }
            }
        }

        public void RemoveAllMonitoredItems()
        {
            lock (_monitoredItemLock)
            {
                // remove all monitored items from all api subscriptions
                foreach (var apiSubscription in _apiSubscriptionDictionary)
                {
                    apiSubscription.Value.RemoveAllMonitoredItems();
                }

                // clear backing dictionary
                _expNodeIdToSubscriptionDictionary.Clear();
            }
        }
        #endregion

        #region event handling
        private void Notification_MonitoredItem(MonitoredItem monitoredItem, MonitoredItemNotificationEventArgs e)
        {
            if (monitoredItem == null || e?.NotificationValue is not MonitoredItemNotification notification)
            {
                return;
            }

            var dataValue = notification.Value;
            if (dataValue == null || StatusCode.IsBad(dataValue.StatusCode))
            {
                return;
            }

            var value = dataValue.WrappedValue.Value;
            var typeInfo = dataValue.WrappedValue.TypeInfo;
            if (!string.IsNullOrEmpty(monitoredItem.IndexRange)
                && !OpcUaTagAddress.TryUnwrapElement(dataValue.WrappedValue, out value, out typeInfo))
            {
                return;
            }

            DoOnMonitoredValue(
                monitoredItem.DisplayName,
                dataValue.SourceTimestamp,
                value,
                typeInfo);
        }

        public event EventHandler<MonitoredValueNotificationEventArgs> OnMonitoredValue;
        private void DoOnMonitoredValue(string expNodeIdString, DateTime sourceTime, object value, TypeInfo dataType)
        {
            OnMonitoredValue?.Invoke(this,
                new MonitoredValueNotificationEventArgs(expNodeIdString, sourceTime, value, dataType));
        }
        #endregion

        #region helper func
        private ApiSubscription GetOrCreateApiSubscription(int samplingIntevall)
        {
            // every sampling intervall has its own subscription
            // lets see if one is already created
            if (_apiSubscriptionDictionary.ContainsKey(samplingIntevall))
            {
                return _apiSubscriptionDictionary[samplingIntevall];
            }

            else
            {
                // create and return a new api subscription object
                var apiSubs = new ApiSubscription(this, _serverSession, samplingIntevall, Notification_MonitoredItem);
                _apiSubscriptionDictionary.Add(samplingIntevall, apiSubs);
                return apiSubs;
            }
        }

        private void AddInternalMonitoredItems(List<MonitoredItemInfo> monitoredItemInfos)
        {
            // create temporary dictionary to map api subscription to a list of added monitored item infos
            var apiSubscriptionDict = new Dictionary<ApiSubscription, List<MonitoredItemInfo>>();

            // sort and map monitored item infos to api subscription objects
            foreach (var monitoredItemInfo in monitoredItemInfos)
            {
                if (!_expNodeIdToSubscriptionDictionary.ContainsKey(monitoredItemInfo.ExpNodeIdString))
                {
                    // get or create api subscription by sampling interval
                    var apiSubscription = GetOrCreateApiSubscription(monitoredItemInfo.SamplingInterval);

                    // add api subscription to temporary dictionary if not exists
                    if (!apiSubscriptionDict.ContainsKey(apiSubscription))
                        apiSubscriptionDict.Add(apiSubscription, new List<MonitoredItemInfo>());

                    var monitoredItemInfoList = apiSubscriptionDict[apiSubscription];
                    monitoredItemInfoList.Add(monitoredItemInfo);
                    _expNodeIdToSubscriptionDictionary.Add(monitoredItemInfo.ExpNodeIdString, apiSubscription);
                }
                else
                {
                    DoOnClientError($"Item is already monitored: {monitoredItemInfo.ExpNodeIdString}");
                }
            }

            // add monitored item info lists to api subscription
            foreach (var apiSubscription in apiSubscriptionDict)
            {
                apiSubscription.Key.AddMonitoredItems(apiSubscription.Value);
            }
        }

        private void RemoveInternalMonitoredItems(List<string> expNodeIdStrings)
        {
            // create temporary dictionary to map api subscription to a list of node id strings
            var apiSubscriptionDict = new Dictionary<ApiSubscription, List<string>>();

            // sort and map expanded node id strings to api subscription objects
            foreach (var expNodeIdString in expNodeIdStrings)
            {
                if (_expNodeIdToSubscriptionDictionary.ContainsKey(expNodeIdString))
                {
                    // get api subscription by expanded node id string
                    var apiSubscription = _expNodeIdToSubscriptionDictionary[expNodeIdString];

                    // add api subscription to temporary dictionary if not exists
                    if (!apiSubscriptionDict.ContainsKey(apiSubscription))
                        apiSubscriptionDict.Add(apiSubscription, new List<string>());

                    var expNodeIdStringList = apiSubscriptionDict[apiSubscription];
                    expNodeIdStringList.Add(expNodeIdString);
                    _expNodeIdToSubscriptionDictionary.Remove(expNodeIdString);
                }
                else
                {
                    DoOnClientError($"Item is not monitored: {expNodeIdString}");
                }
            }

            // remove monitored items from api subscription
            foreach (var apiSubscription in apiSubscriptionDict)
                apiSubscription.Key.RemoveMonitoredItems(apiSubscription.Value);
        }
        #endregion

        #region helper classes
        private sealed class AlarmPresentation
        {
            public static readonly AlarmPresentation Empty = new();
            public int[] SymbolIndexes { get; init; } = [];
            public int[] TextColorIndexes { get; init; } = [];
            public int[] BackColorIndexes { get; init; } = [];
            public int[] FlashingIndexes { get; init; } = [];
            public int[] ClassIndexes { get; init; } = [];
            public Dictionary<string, string> EventTypeNames { get; init; } = new(StringComparer.Ordinal);
        }

        private sealed class AlarmEventSubscription : IDisposable
        {
            private static readonly QualifiedName ConditionIdBrowseName = new("ConditionId");
            private static readonly QualifiedName StateIdBrowseName = new("Id");

            private readonly OpcUaClientApi _clientApi;
            private Session _session;
            private readonly MonitoredItemNotificationEventHandler _notification;
            private Subscription _subscription;
            private readonly List<MonitoredItem> _monitoredItems = [];

            public AlarmEventSubscription(OpcUaClientApi clientApi, Session session, MonitoredItemNotificationEventHandler notification)
            {
                _clientApi = clientApi;
                _session = session;
                _notification = notification;
            }

            // False when the server refused the initial ConditionRefresh.
            public bool Start()
            {
                EventFilter filter;
                AlarmPresentation presentation;
                try
                {
                    filter = BuildEventFilter(out presentation);
                }
                catch (Exception ex)
                {
                    _clientApi.DoOnClientMessage($"Alarm type browse failed: {ex.Message}");
                    filter = BuildCoreFilter();
                    presentation = AlarmPresentation.Empty;
                }

                _clientApi.ClearAlarmNames();
                _clientApi._alarmPresentation = presentation;

                _subscription = new Subscription(_session.DefaultSubscription)
                {
                    PublishingEnabled = true,
                    PublishingInterval = 1000,
                    KeepAliveCount = 10,
                    LifetimeCount = 100,
                    MaxNotificationsPerPublish = 100
                };

                _session.AddSubscription(_subscription);
                _subscription.Create();

                foreach (var nodeId in FindEventNotifierNodes())
                {
                    var monitoredItem = new MonitoredItem(_subscription.DefaultItem)
                    {
                        StartNodeId = nodeId,
                        AttributeId = Attributes.EventNotifier,
                        MonitoringMode = MonitoringMode.Reporting,
                        QueueSize = EventLogConstants.AlarmEventQueueSize,
                        DiscardOldest = true,
                        Filter = filter
                    };
                    monitoredItem.Notification += _notification;
                    _monitoredItems.Add(monitoredItem);
                    _subscription.AddItem(monitoredItem);
                }

                _subscription.ApplyChanges();
                foreach (var monitoredItem in _monitoredItems)
                {
                    var status = monitoredItem.Status?.Error ?? StatusCodes.Good;
                    _clientApi.DoOnClientMessage(
                        $"Alarm source {monitoredItem.StartNodeId} status {status}.");
                }

                return Refresh();
            }

            public bool Refresh()
            {
                if (_subscription == null)
                {
                    return false;
                }

                // The SDK catches a refused method call and only returns false.
                try
                {
                    if (!_subscription.ConditionRefresh())
                    {
                        _clientApi.DoOnClientMessage("Condition refresh failed: the server refused the call.");
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _clientApi.DoOnClientMessage($"Condition refresh failed: {ex.Message}");
                    return false;
                }
            }

            public void Dispose()
            {
                try
                {
                    foreach (var monitoredItem in _monitoredItems)
                    {
                        monitoredItem.Notification -= _notification;
                    }

                    if (_subscription != null)
                    {
                        _subscription.Delete(true);
                    }
                }
                catch
                {
                    // Best-effort cleanup on disconnect.
                }

                _monitoredItems.Clear();
                _subscription = null;
            }

            // Server already reports events from subordinate notifiers.
            // A second subscription to those nodes stores each alarm twice.
            private static List<NodeId> FindEventNotifierNodes()
            {
                return [ObjectIds.Server];
            }

            private EventFilter BuildEventFilter(out AlarmPresentation presentation)
            {
                var catalog = DiscoverTypes();
                var filter = BuildCoreFilter();
                var symbol = new List<int>();
                var textColor = new List<int>();
                var backColor = new List<int>();
                var flashing = new List<int>();
                var alarmClass = new List<int>();
                AddPropertyClauses(filter, catalog, "ALARMCLASSSYMBOL", symbol);
                AddPropertyClauses(filter, catalog, "TEXTCOLOR", textColor);
                AddPropertyClauses(filter, catalog, "BACKCOLOR", backColor);
                AddPropertyClauses(filter, catalog, "FLASHING", flashing);
                AddPropertyClauses(filter, catalog, "ALARMCLASS", alarmClass);
                presentation = new AlarmPresentation
                {
                    SymbolIndexes = symbol.ToArray(),
                    TextColorIndexes = textColor.ToArray(),
                    BackColorIndexes = backColor.ToArray(),
                    FlashingIndexes = flashing.ToArray(),
                    ClassIndexes = alarmClass.ToArray(),
                    EventTypeNames = catalog.TypeNames
                };
                var clauseCount = symbol.Count + textColor.Count + backColor.Count + flashing.Count + alarmClass.Count;
                _clientApi.DoOnClientMessage(
                    $"Alarm types {catalog.TypeNames.Count}, class fields {clauseCount}.");
                return filter;
            }

            private static EventFilter BuildCoreFilter()
            {
                var filter = new EventFilter();
                var baseEvent = ObjectTypeIds.BaseEventType;
                var condition = ObjectTypeIds.ConditionType;
                var acknowledgeable = ObjectTypeIds.AcknowledgeableConditionType;
                var alarm = ObjectTypeIds.AlarmConditionType;

                AddSelect(filter, baseEvent, BrowseNames.EventId);
                AddSelect(filter, baseEvent, BrowseNames.EventType);
                AddSelect(filter, baseEvent, BrowseNames.Time);
                AddSelect(filter, baseEvent, BrowseNames.Message);
                AddSelect(filter, baseEvent, BrowseNames.Severity);
                AddSelect(filter, condition, BrowseNames.ConditionName);
                AddSelect(filter, condition, ConditionIdBrowseName);
                AddSelect(filter, condition, BrowseNames.BranchId);
                AddSelect(filter, condition, BrowseNames.Retain);
                AddSelectStateId(filter, condition, BrowseNames.EnabledState);
                AddSelectStateId(filter, alarm, BrowseNames.ActiveState);
                AddSelect(filter, alarm, BrowseNames.ActiveState, BrowseNames.TransitionTime);
                AddSelectStateId(filter, acknowledgeable, BrowseNames.AckedState);
                AddSelectStateId(filter, acknowledgeable, BrowseNames.ConfirmedState);
                AddSelect(filter, baseEvent, BrowseNames.SourceName);
                AddSelect(filter, alarm, BrowseNames.ActiveState);
                AddSelect(filter, acknowledgeable, BrowseNames.AckedState);
                AddSelectStateId(filter, alarm, BrowseNames.SuppressedState);
                AddSelectStateId(filter, alarm, BrowseNames.OutOfServiceState);
                AddSelect(filter, alarm, BrowseNames.SuppressedOrShelved);
                AddSelect(filter, alarm, BrowseNames.LimitState, BrowseNames.CurrentState);
                AddSelect(filter, baseEvent, BrowseNames.ReceiveTime);
                AddSelectNodeId(filter, condition);

                // WinCC RaiseClear alarms (Notification) are HmiConditionType.
                // OfType AlarmConditionType skips them. ConditionType includes
                // both HmiConditionType and HmiAlarmType.
                var element = new ContentFilterElement
                {
                    FilterOperator = FilterOperator.OfType
                };
                element.FilterOperands.Add(new ExtensionObject(new LiteralOperand
                {
                    Value = new Variant((NodeId)ObjectTypeIds.ConditionType)
                }));
                filter.WhereClause.Elements.Add(element);
                var overflow = new ContentFilterElement
                {
                    FilterOperator = FilterOperator.OfType
                };
                overflow.FilterOperands.Add(new ExtensionObject(new LiteralOperand
                {
                    Value = new Variant((NodeId)ObjectTypeIds.EventQueueOverflowEventType)
                }));
                filter.WhereClause.Elements.Add(overflow);
                var either = new ContentFilterElement
                {
                    FilterOperator = FilterOperator.Or
                };
                either.FilterOperands.Add(new ExtensionObject(new ElementOperand { Index = 0 }));
                either.FilterOperands.Add(new ExtensionObject(new ElementOperand { Index = 1 }));
                filter.WhereClause.Elements.Add(either);
                return filter;
            }

            private sealed class AlarmTypeCatalog
            {
                public AlarmTypeCatalog()
                {
                    foreach (var name in AlarmClassPropertyNames)
                    {
                        Properties[name] = [];
                    }
                }

                public Dictionary<string, string> TypeNames { get; } = new(StringComparer.Ordinal);
                public Dictionary<string, List<AlarmPropertyDeclaration>> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
            }

            private readonly struct AlarmPropertyDeclaration
            {
                public AlarmPropertyDeclaration(NodeId typeId, QualifiedName name)
                {
                    TypeId = typeId;
                    Name = name;
                }

                public NodeId TypeId { get; }
                public QualifiedName Name { get; }
            }

            private static void AddPropertyClauses(
                EventFilter filter,
                AlarmTypeCatalog catalog,
                string propertyName,
                List<int> indexes)
            {
                if (!catalog.Properties.TryGetValue(propertyName, out var declarations))
                {
                    return;
                }

                foreach (var declaration in declarations)
                {
                    if (indexes.Count >= MaxAlarmPropertyClauses
                        || declaration.TypeId == null
                        || declaration.Name == null
                        || string.IsNullOrEmpty(declaration.Name.Name))
                    {
                        continue;
                    }

                    AddSelect(filter, declaration.TypeId, declaration.Name);
                    indexes.Add(filter.SelectClauses.Count - 1);
                }
            }

            private AlarmTypeCatalog DiscoverTypes()
            {
                try
                {
                    return WalkConditionTypes();
                }
                finally
                {
                    try
                    {
                        _session?.NodeCache?.Clear();
                    }
                    catch
                    {
                        // Clearing the node cache is best-effort.
                    }
                }
            }

            private AlarmTypeCatalog WalkConditionTypes()
            {
                var catalog = new AlarmTypeCatalog();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var level = new List<NodeId> { ObjectTypeIds.ConditionType };
                catalog.TypeNames[ObjectTypeIds.ConditionType.ToString()] = "ConditionType";

                for (var depth = 0; depth <= MaxConditionTypeDepth && level.Count > 0; depth++)
                {
                    var kept = new List<NodeId>();
                    foreach (var typeId in level)
                    {
                        if (typeId == null)
                        {
                            continue;
                        }

                        if (seen.Count >= MaxConditionTypes)
                        {
                            break;
                        }

                        var key = typeId.ToString();
                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        kept.Add(typeId);
                    }

                    if (kept.Count == 0)
                    {
                        break;
                    }

                    var properties = BrowseLevel(kept, ReferenceTypeIds.HasProperty, (uint)NodeClass.Variable);
                    for (var i = 0; i < kept.Count && i < properties.Count; i++)
                    {
                        CollectProperties(catalog, kept[i], properties[i]);
                    }

                    if (depth == MaxConditionTypeDepth)
                    {
                        break;
                    }

                    var subtypes = BrowseLevel(kept, ReferenceTypeIds.HasSubtype, (uint)NodeClass.ObjectType);
                    var next = new List<NodeId>();
                    for (var i = 0; i < kept.Count && i < subtypes.Count; i++)
                    {
                        foreach (var reference in subtypes[i])
                        {
                            var child = ToNodeId(reference?.NodeId);
                            var name = reference?.BrowseName?.Name;
                            if (child == null || string.IsNullOrEmpty(name))
                            {
                                continue;
                            }

                            catalog.TypeNames[child.ToString()] = name;
                            next.Add(child);
                        }
                    }

                    level = next;
                }

                return catalog;
            }

            private static void CollectProperties(
                AlarmTypeCatalog catalog,
                NodeId typeId,
                List<ReferenceDescription> references)
            {
                if (references == null)
                {
                    return;
                }

                foreach (var reference in references)
                {
                    var name = reference?.BrowseName;
                    if (name == null || string.IsNullOrEmpty(name.Name))
                    {
                        continue;
                    }

                    if (!catalog.Properties.TryGetValue(name.Name, out var list) || list.Count >= MaxAlarmPropertyClauses)
                    {
                        continue;
                    }

                    list.Add(new AlarmPropertyDeclaration(typeId, name));
                }
            }

            private List<List<ReferenceDescription>> BrowseLevel(
                IReadOnlyList<NodeId> nodeIds,
                NodeId referenceTypeId,
                uint nodeClassMask)
            {
                var grouped = new List<List<ReferenceDescription>>(nodeIds.Count);
                for (var i = 0; i < nodeIds.Count; i++)
                {
                    grouped.Add([]);
                }

                if (nodeIds.Count == 0)
                {
                    return grouped;
                }

                var descriptions = new BrowseDescriptionCollection();
                foreach (var nodeId in nodeIds)
                {
                    descriptions.Add(new BrowseDescription
                    {
                        NodeId = nodeId,
                        BrowseDirection = BrowseDirection.Forward,
                        ReferenceTypeId = referenceTypeId,
                        IncludeSubtypes = false,
                        NodeClassMask = nodeClassMask,
                        ResultMask = (uint)BrowseResultMask.BrowseName
                    });
                }

                _session.Browse(
                    null,
                    null,
                    AlarmTypeBrowseLimit,
                    descriptions,
                    out BrowseResultCollection results,
                    out _);

                if (results == null)
                {
                    return grouped;
                }

                for (var i = 0; i < nodeIds.Count && i < results.Count; i++)
                {
                    TakeReferences(results[i], grouped[i]);
                }

                return grouped;
            }

            private void TakeReferences(BrowseResult result, List<ReferenceDescription> into)
            {
                if (result == null || StatusCode.IsBad(result.StatusCode))
                {
                    ReleaseContinuation(result?.ContinuationPoint);
                    return;
                }

                if (result.References != null)
                {
                    into.AddRange(result.References);
                }

                var point = result.ContinuationPoint;
                var rounds = 0;
                while (point != null && point.Length > 0 && into.Count < MaxConditionTypes && rounds < MaxContinuationRounds)
                {
                    var points = new ByteStringCollection { point };
                    _session.BrowseNext(null, false, points, out BrowseResultCollection more, out _);
                    point = null;
                    rounds++;
                    if (more == null || more.Count == 0)
                    {
                        break;
                    }

                    var next = more[0];
                    if (next?.References != null)
                    {
                        into.AddRange(next.References);
                    }

                    point = next?.ContinuationPoint;
                }

                ReleaseContinuation(point);
            }

            private void ReleaseContinuation(byte[] point)
            {
                if (point == null || point.Length == 0)
                {
                    return;
                }

                try
                {
                    var points = new ByteStringCollection { point };
                    _session.BrowseNext(null, true, points, out _, out _);
                }
                catch
                {
                    // Releasing a continuation point is best-effort.
                }
            }

            private NodeId ToNodeId(ExpandedNodeId expanded)
            {
                if (expanded == null)
                {
                    return null;
                }

                try
                {
                    return ExpandedNodeId.ToNodeId(expanded, _session.NamespaceUris);
                }
                catch
                {
                    return null;
                }
            }

            // Empty browse path reads the condition instance. Field index AlarmFieldConditionNodeId.
            private static void AddSelectNodeId(EventFilter filter, NodeId typeId)
            {
                filter.SelectClauses.Add(new SimpleAttributeOperand
                {
                    TypeDefinitionId = typeId,
                    AttributeId = Attributes.NodeId,
                    BrowsePath = []
                });
            }

            private static void AddSelect(EventFilter filter, NodeId typeId, QualifiedName browseName)
            {
                filter.SelectClauses.Add(new SimpleAttributeOperand
                {
                    TypeDefinitionId = typeId,
                    AttributeId = Attributes.Value,
                    BrowsePath = [browseName]
                });
            }

            private static void AddSelect(EventFilter filter, NodeId typeId, QualifiedName first, QualifiedName second)
            {
                filter.SelectClauses.Add(new SimpleAttributeOperand
                {
                    TypeDefinitionId = typeId,
                    AttributeId = Attributes.Value,
                    BrowsePath = [first, second]
                });
            }

            private static void AddSelectStateId(EventFilter filter, NodeId typeId, QualifiedName stateName)
            {
                filter.SelectClauses.Add(new SimpleAttributeOperand
                {
                    TypeDefinitionId = typeId,
                    AttributeId = Attributes.Value,
                    BrowsePath = [stateName, StateIdBrowseName]
                });
            }
        }

        /// <summary>
        /// Helper class to handle subscription objects with associated monitored items
        /// </summary>
        private class ApiSubscription
        {
            #region member
            private OpcUaClientApi _clientApi;
            private Session _serverSession;
            private SubscriptionChangeMask _subscriptionState = SubscriptionChangeMask.None;
            private readonly int _samplingIntervall;
            private readonly MonitoredItemNotificationEventHandler _notification;

            private Subscription _opcUaSubscription;
            private readonly Dictionary<string, MonitoredItemInfo> _monitoredItemInfos = new Dictionary<string, MonitoredItemInfo>();
            private readonly Dictionary<string, MonitoredItem> _monitoredItems = new Dictionary<string, MonitoredItem>();
            #endregion

            #region construction
            public ApiSubscription(OpcUaClientApi clientApi, Session serverSession, int samplingIntervall,
                MonitoredItemNotificationEventHandler notification)
            {
                _clientApi = clientApi;
                _serverSession = serverSession;
                _samplingIntervall = samplingIntervall;
                _notification = notification;
                InitOpcUaSubscription();
            }
            #endregion

            #region public interface
            public void OnSessionReconnect(Session newServerSession)
            {
                _serverSession = newServerSession;

                // recreate subscription on reconnect
                InitOpcUaSubscription();

                // clear monitored items dictionary -> will be recreated
                _monitoredItems.Clear();

                AddMonitoredItemsToOpcUaSubscription(_monitoredItemInfos.Values.ToList());
            }

            public void AddMonitoredItems(List<MonitoredItemInfo> monitoredItemInfos)
            {
                var items = new List<MonitoredItemInfo>();
                foreach (var mii in monitoredItemInfos)
                {
                    if (!_monitoredItemInfos.ContainsKey(mii.ExpNodeIdString))
                    {
                        _monitoredItemInfos.Add(mii.ExpNodeIdString, mii);
                        items.Add(mii);
                    }
                }
                AddMonitoredItemsToOpcUaSubscription(items);
            }

            public void RemoveMonitoredItems(List<string> expNodeIdStrings)
            {
                var items = new List<string>();
                foreach (var expNodeIdString in expNodeIdStrings)
                {
                    if (_monitoredItemInfos.ContainsKey(expNodeIdString))
                    {
                        _monitoredItemInfos.Remove(expNodeIdString);
                        items.Add(expNodeIdString);
                    }
                }
                RemoveMonitoredItemsFromOpcUaSubscription(items);
            }

            public void RemoveAllMonitoredItems()
            {
                var allItems = new List<string>(_monitoredItemInfos.Keys);
                RemoveMonitoredItems(allItems);
            }
            #endregion

            #region opc ua subscription 
            private void InitOpcUaSubscription()
            {
                // create a new opc ua subsciption object
                _opcUaSubscription = new Subscription(_serverSession.DefaultSubscription)
                {
                    PublishingEnabled = true,
                    PublishingInterval = _samplingIntervall,
                };

                _opcUaSubscription.StateChanged += UaSubscription_StateChanged;

                // add to opc ua server session
                _serverSession.AddSubscription(_opcUaSubscription);
                _opcUaSubscription.Create();
            }

            private void AddMonitoredItemsToOpcUaSubscription(List<MonitoredItemInfo> monitoredItemInfos)
            {
                var monitoredItems = CreateMonitoredItemsByMonitoredItemInfos(monitoredItemInfos);

                // add the monitored items to backing dictionary
                foreach (var monitoredItemInfo in monitoredItemInfos)
                {
                    var key = monitoredItemInfo.ExpNodeIdString;
                    if (!_monitoredItems.ContainsKey(key))
                    {
                        if (monitoredItems.ContainsKey(key))
                            _monitoredItems.Add(key, monitoredItems[key]);
                    }
                }

                // split large list into chunks
                var chunks = SplitMonitoredItemsList(monitoredItems.Values.ToList());

                try
                {
                    foreach (var chunk in chunks)
                    {
                        // add all items to the opc ua subscription
                        _opcUaSubscription.AddItems(chunk);

                        // apply changes to the opc ua subscription
                        _opcUaSubscription.ApplyChanges();
                    }
                }
                catch (Exception e)
                {
                    _clientApi.DoOnClientError($"{GetLogPfx()} {e.Message}");
                    _opcUaSubscription.Delete(true);
                    throw new Exception(e.Message, e);
                }
            }

            private void RemoveMonitoredItemsFromOpcUaSubscription(List<string> monitoredItemKeys)
            {
                var monitoredItems = new List<MonitoredItem>();
                foreach (var monitoredItemKey in monitoredItemKeys)
                {
                    if (_monitoredItems.ContainsKey(monitoredItemKey))
                    {
                        var monitoredItem = _monitoredItems[monitoredItemKey];
                        _monitoredItems.Remove(monitoredItemKey);
                        monitoredItems.Add(monitoredItem);
                    }
                }

                // split large list into chunks
                var chunks = SplitMonitoredItemsList(monitoredItems);

                try
                {
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var chunk = chunks[i];
                        if (chunk.Count == 0)
                        {
                            continue;
                        }

                        _opcUaSubscription.RemoveItems(chunk);
                        _opcUaSubscription.ApplyChanges();
                    }
                }
                catch (Exception e)
                {
                    _clientApi.DoOnClientError($"{GetLogPfx()} {e.Message}");
                    _opcUaSubscription.Delete(true);
                    throw new Exception(e.Message, e);
                }
            }

            private List<List<MonitoredItem>> SplitMonitoredItemsList(List<MonitoredItem> monitoredItems)
            {
                var result = new List<List<MonitoredItem>>();
                var list = new List<MonitoredItem>();

                for (int i = 0; i < monitoredItems.Count; i++)
                {
                    if (i % _clientApi.MaxNodesPerRegisterNodes == 0 && i > 0)
                    {
                        if (list.Count > 0) result.Add(list);
                        list = [];
                    }
                    list.Add(monitoredItems[i]);
                }
                result.Add(list);
                return result;
            }

            private MonitoredItem CreateMonitoredItemByMonitoredItemInfo(MonitoredItemInfo monitoredItemInfo)
            {
                if (!OpcUaTagAddress.TryParse(monitoredItemInfo.ExpNodeIdString, out var nodeAddress, out var indexRange))
                {
                    throw new FormatException($"Invalid OPC UA address: {monitoredItemInfo.ExpNodeIdString}");
                }

                ExpandedNodeId expNodeId = ExpandedNodeId.Parse(nodeAddress);
                NodeId nodeId = ExpandedNodeId.ToNodeId(expNodeId, _serverSession.NamespaceUris);

                MonitoredItem monitoredItem = new MonitoredItem
                {
                    DisplayName = monitoredItemInfo.ExpNodeIdString,
                    StartNodeId = nodeId,
                    AttributeId = Attributes.Value,
                    IndexRange = indexRange,
                    MonitoringMode = MonitoringMode.Reporting,
                    SamplingInterval = _samplingIntervall,
                    QueueSize = monitoredItemInfo.QueueSize,
                    DiscardOldest = true
                };

                // add notification event handler
                monitoredItem.Notification += _notification;

                return monitoredItem;
            }

            private Dictionary<string, MonitoredItem> CreateMonitoredItemsByMonitoredItemInfos(List<MonitoredItemInfo> monitoredItemInfos)
            {
                var result = new Dictionary<string, MonitoredItem>();

                foreach (var monitoredItemInfo in monitoredItemInfos)
                {
                    var key = monitoredItemInfo.ExpNodeIdString;
                    result.Add(key, CreateMonitoredItemByMonitoredItemInfo(monitoredItemInfo));
                }

                return result;
            }

            private string GetLogPfx()
            {
                return $"ApiSubscription_{_samplingIntervall}";
            }
            #endregion

            #region events
            private void UaSubscription_StateChanged(Subscription subscription, SubscriptionStateChangedEventArgs e)
            {
                _subscriptionState = e.Status;
                _clientApi.DoOnClientMessage($"{GetLogPfx()} state changed {e.Status}");
            }
            #endregion
        }
        #endregion
    }

    #region event args classes
    public class ClientMessageEventArgs : EventArgs
    {
        public string Message { get; set; }

        public ClientMessageEventArgs(string message)
        {
            Message = message;
        }
    }

    public class StateChangeEventArgs : EventArgs
    {
        public ConnectionState State { get; set; }

        public StateChangeEventArgs(ConnectionState state)
        {
            State = state;
        }
    }

    public class ServerKeepAliveEventArgs : EventArgs
    {
        public DateTime ServerTime { get; set; }

        public ServerKeepAliveEventArgs(KeepAliveEventArgs e)
        {
            ServerTime = e.CurrentTime;
        }

        public ServerKeepAliveEventArgs(DateTime serverTime)
        {
            ServerTime = serverTime;
        }
    }

    public class BrowseResultItemEventArgs : EventArgs
    {
        public BrowseResultItem Result { get; set; }

        public BrowseResultItemEventArgs(BrowseResultItem result)
        {
            Result = result;
        }
    }

    public class MonitoredValueNotificationEventArgs : EventArgs
    {
        public string ExpNodeIdString { get; set; }
        public DateTime SourceTimestamp { get; set; }
        public object Value { get; set; }
        public TypeInfo DataType { get; set; }
        public MonitoredValueNotificationEventArgs(string expNodeIdString, DateTime sourceTimestamp, object value, TypeInfo dataType)
        {
            ExpNodeIdString = expNodeIdString;
            SourceTimestamp = sourceTimestamp;
            Value = value;
            DataType = dataType;
        }
    }
    #endregion

    #region helper classes
    public class BrowseResultItem
    {
        public ExpandedNodeId ExpNodeId { get; set; }
        public int Sequence { get; set; }
        public bool HasChildren { get; set; }
    }

    public class OpcUaBrowseProgress
    {
        public string Phase { get; set; }
        public int NodesBrowsed { get; set; }
        public int VariablesFound { get; set; }
        public int ScalarTags { get; set; }
        public int StructuresExpanded { get; set; }
        public int BrowseFailures { get; set; }
        // What the walk is doing inside one step, e.g. the data types of a big chunk; null between steps.
        public string Detail { get; set; }
    }

    public class OpcUaVariableNode
    {
        public NodeId NodeId { get; set; }
        public string DisplayName { get; set; }
        public string BrowseName { get; set; }
    }

    public class OpcUaScalarVariable
    {
        public string Address { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public int TypeId { get; set; }
    }

    public class MonitoredItemInfo
    {
        public string ExpNodeIdString { get; set; }
        public int SamplingInterval { get; set; } = 1000;
        public uint QueueSize { get; set; } = 20;
    }

    public sealed class OpcUaAuxSession : IDisposable
    {
        public Session Session { get; }
        public IReadOnlyList<string> Namespaces { get; }
        public int MaxNodesPerBrowse { get; }
        public int MaxNodesPerRead { get; }

        public OpcUaAuxSession(Session session, List<string> namespaces, int maxNodesPerBrowse, int maxNodesPerRead)
        {
            Session = session;
            Namespaces = namespaces ?? [];
            MaxNodesPerBrowse = maxNodesPerBrowse;
            MaxNodesPerRead = maxNodesPerRead;
        }

        public void Dispose()
        {
            if (Session == null)
            {
                return;
            }

            try
            {
                Session.Close();
            }
            catch
            {
                // Closing a browse session is best-effort.
            }

            try
            {
                Session.Dispose();
            }
            catch
            {
                // Disposing a closed browse session is best-effort.
            }
        }
    }

    public interface IOpcUaBrowseStore
    {
        void SetInflight(string phase, IReadOnlyList<string> nodeKeys);
        void CommitBrowse(
            IReadOnlyList<string> finishedParents,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<BrowseCheckpointNode> newVariables);
        void CommitClassify(
            IReadOnlyList<string> finishedVariables,
            IReadOnlyList<string> newObjects,
            IReadOnlyList<DataTagBrowseImportItem> scalars);
        /// <summary>Gives existing tags of the source a new address (old -> new); a new address already taken stays.</summary>
        void RenameTags(IReadOnlyList<KeyValuePair<string, string>> renames);
    }

    public class OpcUaConnectionTestResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";

        /// <summary>The endpoints the server offers, empty when they could not be read.</summary>
        public List<OpcUaEndpointInfo> Endpoints { get; set; } = [];

        /// <summary>The certificate of the endpoint the session uses, null when unknown.</summary>
        public OpcUaCertificateInfo ServerCertificate { get; set; }

        /// <summary>What the user must decide before the test signs in, one of <see cref="OpcUaCertificateDecision"/>; null when nothing.</summary>
        public string CertificateDecision { get; set; }

        /// <summary>The server refused the secure channel: it does not trust the client certificate of DataLogXE.</summary>
        public bool ClientNotTrusted { get; set; }

        /// <summary>The server does not allow an anonymous session: the source needs a user name and password.</summary>
        public bool SignInRequired { get; set; }
        public string ProductName { get; set; } = "";
        public List<string> Locales { get; set; } = [];

        /// <summary>
        /// Server.ServerCapabilities.MinSupportedSampleRate in ms, null when the server does not report it.
        /// </summary>
        public int? MinSampleRate { get; set; }

        /// <summary>Server clock minus DataLogXE clock in ms (positive: the server is ahead), null when unknown.</summary>
        public long? ClockOffsetMs { get; set; }

        public string EndpointUrl { get; set; } = "";
        public string SecurityPolicy { get; set; } = "";
        public string SecurityMode { get; set; } = "";
        public string UserIdentity { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string ApplicationUri { get; set; } = "";
        public string ManufacturerName { get; set; } = "";
        public string SoftwareVersion { get; set; } = "";
        public string BuildNumber { get; set; } = "";
        public string ServerState { get; set; } = "";
        public DateTime? StartTimeUtc { get; set; }
        public int? NamespaceCount { get; set; }
        public long? MaxNodesPerRead { get; set; }
        public long? MaxNodesPerBrowse { get; set; }
        public long? MaxMonitoredItemsPerCall { get; set; }
        public long? CurrentSessionCount { get; set; }
        public long? CurrentSubscriptionCount { get; set; }
        /// <summary>The Server object can be subscribed to for events (alarms and conditions), null when unknown.</summary>
        public bool? EventsSupported { get; set; }
    }

    public sealed class OpcUaBrowseInterruptedException : Exception
    {
        public OpcUaBrowseInterruptedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
    #endregion
}
