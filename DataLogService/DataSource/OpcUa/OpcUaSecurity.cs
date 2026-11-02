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
using Opc.Ua;
using System.Security.Cryptography.X509Certificates;

namespace DataLogService.DataSource.OpcUa
{
    /// <summary>
    /// The security of the OPC UA sessions of one data source: message security, policy and how the server certificate
    /// is checked. <see cref="Unsecured"/> is the behaviour up to 0.0.199 (no security, every certificate accepted).
    /// </summary>
    public sealed class OpcUaSecuritySettings
    {
        public static readonly OpcUaSecuritySettings Unsecured = new(
            OpcUaSecurityValues.ModeNone, OpcUaSecurityValues.PolicyBest, OpcUaSecurityValues.TrustAny, "");

        public OpcUaSecuritySettings(string mode, string policy, string trust, string pinnedThumbprint)
        {
            Mode = OpcUaSecurityValues.Modes.Contains(mode) ? mode : OpcUaSecurityValues.ModeNone;
            Policy = string.IsNullOrWhiteSpace(policy) ? OpcUaSecurityValues.PolicyBest : policy.Trim();
            Trust = OpcUaSecurityValues.Trusts.Contains(trust) ? trust : OpcUaSecurityValues.TrustAny;
            PinnedThumbprint = OpcUaSecurityValues.NormalizeThumbprint(pinnedThumbprint);
        }

        public string Mode { get; }
        public string Policy { get; }
        public string Trust { get; }
        public string PinnedThumbprint { get; }

        public bool IsUnsecured => Mode == OpcUaSecurityValues.ModeNone;

        /// <summary>The server certificate is only checked when the messages are secured.</summary>
        public bool AcceptsAnyCertificate => IsUnsecured || Trust == OpcUaSecurityValues.TrustAny;

        public MessageSecurityMode MessageSecurityMode => Mode switch
        {
            OpcUaSecurityValues.ModeSign => MessageSecurityMode.Sign,
            OpcUaSecurityValues.ModeSignAndEncrypt => MessageSecurityMode.SignAndEncrypt,
            _ => MessageSecurityMode.None
        };

        public static OpcUaSecuritySettings FromModel(DataSourceModel model)
        {
            return new OpcUaSecuritySettings(model.OpcUaSecurityMode, model.OpcUaSecurityPolicy, model.OpcUaCertTrust,
                model.OpcUaServerCertThumbprint);
        }

        public override string ToString()
        {
            return IsUnsecured
                ? "None"
                : $"{MessageSecurityMode}, policy {Policy}, certificate {(AcceptsAnyCertificate ? "any" : "pinned")}";
        }
    }

    /// <summary>
    /// Picks the endpoint of a server that matches the wanted security.
    /// </summary>
    public static class OpcUaEndpointSelector
    {
        /// <summary>
        /// The strength of the policies; "best" takes the strongest the server offers in the wanted mode.
        /// </summary>
        private static int Rank(string policyUri)
        {
            return policyUri switch
            {
                SecurityPolicies.Aes256_Sha256_RsaPss => 5,
                SecurityPolicies.Basic256Sha256 => 4,
                SecurityPolicies.Aes128_Sha256_RsaOaep => 3,
                SecurityPolicies.Basic256 => 1,
                SecurityPolicies.Basic128Rsa15 => 0,
                _ => -1
            };
        }

        /// <summary>
        /// The secured endpoint an unsecured source takes when the server offers no endpoint without security (a
        /// SIMATIC S7-1500 offers SignAndEncrypt only); null when the server offers one without security, or offers
        /// nothing known. Up to OPC UA stack 1.5.376 the session picked such an endpoint by itself; since 1.5.378 it
        /// refuses, so the choice is made here: SignAndEncrypt before Sign, then the policy ranking.
        /// </summary>
        public static EndpointDescription SelectInsteadOfUnsecured(IEnumerable<EndpointDescription> endpoints,
            string discoveryUrl)
        {
            var offered = (endpoints ?? []).Where(IsTcp).ToList();
            if (offered.Count == 0 || offered.Any(e => e.SecurityMode == MessageSecurityMode.None))
            {
                return null;
            }

            var chosen = offered
                .Where(e => Rank(e.SecurityPolicyUri) >= 0)
                .OrderByDescending(e => e.SecurityMode == MessageSecurityMode.SignAndEncrypt)
                .ThenByDescending(e => Rank(e.SecurityPolicyUri))
                .ThenByDescending(e => e.SecurityLevel)
                .FirstOrDefault();
            if (chosen == null)
            {
                return null;
            }

            var result = (EndpointDescription)chosen.Clone();
            result.EndpointUrl = WithAddressOf(chosen.EndpointUrl, discoveryUrl);
            return result;
        }

        /// <summary>
        /// The endpoint for the settings, with the address of <paramref name="discoveryUrl"/> (servers often report a
        /// host name the client cannot resolve); throws with the offered endpoints when none fits.
        /// </summary>
        public static EndpointDescription Select(IEnumerable<EndpointDescription> endpoints,
            OpcUaSecuritySettings settings, string discoveryUrl)
        {
            var offered = (endpoints ?? []).Where(IsTcp).ToList();
            var candidates = offered.Where(e => e.SecurityMode == settings.MessageSecurityMode);
            if (settings.Policy == OpcUaSecurityValues.PolicyBest)
            {
                // Known policies only; the ranking puts the deprecated ones last.
                candidates = candidates.Where(e => Rank(e.SecurityPolicyUri) >= 0);
            }
            else
            {
                var policyUri = PolicyUri(settings.Policy);
                candidates = candidates.Where(e => e.SecurityPolicyUri == policyUri);
            }

            var chosen = candidates
                .OrderByDescending(e => Rank(e.SecurityPolicyUri))
                .ThenByDescending(e => e.SecurityLevel)
                .FirstOrDefault();
            if (chosen == null)
            {
                throw new ServiceResultException(StatusCodes.BadSecurityPolicyRejected,
                    $"The server offers no endpoint with security {settings.MessageSecurityMode} and policy " +
                    $"{settings.Policy}. Offered: {Describe(offered)}");
            }

            var result = (EndpointDescription)chosen.Clone();
            result.EndpointUrl = WithAddressOf(chosen.EndpointUrl, discoveryUrl);
            return result;
        }

        public static string PolicyUri(string policyName)
        {
            if (string.IsNullOrWhiteSpace(policyName))
            {
                return "";
            }

            return policyName.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? policyName
                : SecurityPolicies.BaseUri + policyName;
        }

        public static string PolicyName(string policyUri)
        {
            if (string.IsNullOrEmpty(policyUri))
            {
                return "";
            }

            return policyUri.StartsWith(SecurityPolicies.BaseUri, StringComparison.Ordinal)
                ? policyUri.Substring(SecurityPolicies.BaseUri.Length)
                : policyUri;
        }

        public static string Describe(IEnumerable<EndpointDescription> endpoints)
        {
            var parts = (endpoints ?? [])
                .Select(e => $"{e.SecurityMode} {PolicyName(e.SecurityPolicyUri)}")
                .Distinct()
                .ToList();
            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static bool IsTcp(EndpointDescription endpoint)
        {
            return endpoint?.EndpointUrl != null
                && endpoint.EndpointUrl.StartsWith(Utils.UriSchemeOpcTcp, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrEmpty(endpoint.TransportProfileUri) || endpoint.TransportProfileUri == Profiles.UaTcpTransport);
        }

        /// <summary>The scheme, host and port of <paramref name="discoveryUrl"/> with the path of the endpoint.</summary>
        internal static string WithAddressOf(string endpointUrl, string discoveryUrl)
        {
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var endpoint)
                || !Uri.TryCreate(discoveryUrl, UriKind.Absolute, out var discovery))
            {
                return discoveryUrl;
            }

            var builder = new UriBuilder(endpoint)
            {
                Scheme = discovery.Scheme,
                Host = discovery.Host,
                Port = discovery.Port
            };
            return builder.Uri.ToString().TrimEnd('/') == discovery.ToString().TrimEnd('/')
                ? discoveryUrl
                : builder.Uri.ToString();
        }
    }

    /// <summary>
    /// What a connection test needs the user to decide before it signs in.
    /// </summary>
    public static class OpcUaCertificateDecision
    {
        /// <summary>No certificate is pinned yet: trust the one the server shows?</summary>
        public const string New = "new";

        /// <summary>The server shows another than the pinned certificate: trust the new one?</summary>
        public const string Changed = "changed";

        /// <summary>A secured session was asked for, but the server offers unsecured endpoints only.</summary>
        public const string UnsecuredOnly = "unsecuredonly";
    }

    /// <summary>
    /// Checks the server certificate of the sessions of one data source: accepts every certificate or only the pinned
    /// one, and remembers a rejected certificate for the error message.
    /// </summary>
    public sealed class OpcUaCertificateGuard
    {
        private readonly OpcUaSecuritySettings _settings;
        private string _lastRejected = "";

        public OpcUaCertificateGuard(OpcUaSecuritySettings settings)
        {
            _settings = settings ?? OpcUaSecuritySettings.Unsecured;
        }

        public bool AcceptsAny => _settings.AcceptsAnyCertificate;

        /// <summary>The thumbprint of the last certificate this guard rejected, empty when none.</summary>
        public string LastRejectedThumbprint => Volatile.Read(ref _lastRejected);

        public void ResetRejected()
        {
            Volatile.Write(ref _lastRejected, "");
        }

        public bool Accepts(string thumbprint)
        {
            if (AcceptsAny)
            {
                return true;
            }

            return _settings.PinnedThumbprint.Length > 0
                && string.Equals(OpcUaSecurityValues.NormalizeThumbprint(thumbprint), _settings.PinnedThumbprint,
                    StringComparison.Ordinal);
        }

        public void OnValidation(CertificateValidator sender, CertificateValidationEventArgs e)
        {
            var thumbprint = e.Certificate?.Thumbprint ?? "";
            if (Accepts(thumbprint))
            {
                // The pin replaces the trust list: also a self-signed or host-mismatched certificate is fine.
                e.Accept = true;
                e.AcceptAll = true;
                return;
            }

            Volatile.Write(ref _lastRejected, OpcUaSecurityValues.NormalizeThumbprint(thumbprint));
            e.Accept = false;
        }

        /// <summary>
        /// The text for a failed connect: names a rejected certificate, or hints at the trust list of the server.
        /// </summary>
        /// <param name="signedIn">Whether the session signs in with a user name (else it is anonymous).</param>
        public string Describe(Exception ex, bool signedIn = false)
        {
            if (IsSignInFailure(ex))
            {
                // Not a matter of the certificates: the server refused the user
                return signedIn
                    ? "The server refused the user name or password."
                    : "The server does not allow an anonymous session: turn on \"Sign in with a user\" and enter the " +
                      "user name and password.";
            }

            var rejected = LastRejectedThumbprint;
            if (rejected.Length > 0)
            {
                return _settings.PinnedThumbprint.Length == 0
                    ? $"Server certificate {rejected} rejected: no certificate is pinned. Run the connection test and " +
                      "take over the server certificate."
                    : $"Server certificate {rejected} rejected: it is not the pinned certificate " +
                      $"{_settings.PinnedThumbprint}. If the server got a new certificate, run the connection test and " +
                      "take it over.";
            }

            if (!_settings.IsUnsecured && ex is ServiceResultException sre && IsTrustFailure(sre.StatusCode))
            {
                return $"{ex.Message} The server may not trust the client certificate of DataLogXE yet: download it " +
                    "under Settings and add it to the trusted certificates of the server.";
            }

            return ex.Message;
        }

        /// <summary>
        /// Whether a secured connect failed because the server does not trust the client certificate of DataLogXE
        /// (not because of the server certificate or the user).
        /// </summary>
        public bool IsClientTrustFailure(Exception ex)
        {
            return LastRejectedThumbprint.Length == 0
                && !_settings.IsUnsecured
                && ex is ServiceResultException sre
                && IsTrustFailure(sre.StatusCode);
        }

        /// <summary>Whether the server refused the user of the session (anonymous, user name or password).</summary>
        public static bool IsSignInFailure(Exception ex)
        {
            return ex is ServiceResultException sre
                && (sre.StatusCode == StatusCodes.BadIdentityTokenRejected
                    || sre.StatusCode == StatusCodes.BadIdentityTokenInvalid
                    || sre.StatusCode == StatusCodes.BadUserAccessDenied
                    || sre.StatusCode == StatusCodes.BadUserSignatureInvalid);
        }

        /// <summary>Whether a failed connect is a matter of the certificates or of the sign-in.</summary>
        public bool IsSecurityFailure(Exception ex)
        {
            return LastRejectedThumbprint.Length > 0
                || IsSignInFailure(ex)
                || (!_settings.IsUnsecured && ex is ServiceResultException sre
                    && (IsTrustFailure(sre.StatusCode) || sre.StatusCode == StatusCodes.BadSecurityPolicyRejected));
        }

        private static bool IsTrustFailure(uint statusCode)
        {
            // A server that does not trust the client certificate often just closes the secure channel.
            return statusCode == StatusCodes.BadSecurityChecksFailed
                || statusCode == StatusCodes.BadSecureChannelClosed
                || statusCode == StatusCodes.BadCertificateUntrusted
                || statusCode == StatusCodes.BadCertificateInvalid
                || statusCode == StatusCodes.BadNoValidCertificates;
        }
    }

    /// <summary>The facts of a certificate shown in the UI.</summary>
    public sealed class OpcUaCertificateInfo
    {
        public string Subject { get; set; } = "";
        public string Issuer { get; set; } = "";
        public string Thumbprint { get; set; } = "";
        public DateTime NotBeforeUtc { get; set; }
        public DateTime NotAfterUtc { get; set; }
        public bool SelfSigned { get; set; }
        public string ApplicationUri { get; set; } = "";

        public static OpcUaCertificateInfo From(X509Certificate2 certificate)
        {
            if (certificate == null)
            {
                return null;
            }

            string applicationUri;
            try
            {
                applicationUri = X509Utils.GetApplicationUriFromCertificate(certificate) ?? "";
            }
            catch
            {
                applicationUri = "";
            }

            return new OpcUaCertificateInfo
            {
                Subject = certificate.Subject,
                Issuer = certificate.Issuer,
                Thumbprint = certificate.Thumbprint,
                NotBeforeUtc = certificate.NotBefore.ToUniversalTime(),
                NotAfterUtc = certificate.NotAfter.ToUniversalTime(),
                SelfSigned = string.Equals(certificate.Subject, certificate.Issuer, StringComparison.Ordinal),
                ApplicationUri = applicationUri
            };
        }

        /// <summary>The first certificate of a chain blob as sent in an endpoint description, null when there is none.</summary>
        public static X509Certificate2 FirstOfChain(byte[] blob)
        {
            if (blob == null || blob.Length == 0)
            {
                return null;
            }

            try
            {
                var chain = Utils.ParseCertificateChainBlob(blob);
                return chain.Count > 0 ? chain[0] : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>One endpoint a server offers, for the connection test.</summary>
    public sealed class OpcUaEndpointInfo
    {
        public string SecurityMode { get; set; } = "";
        public string SecurityPolicy { get; set; } = "";
        public int SecurityLevel { get; set; }
        public string EndpointUrl { get; set; } = "";
    }
}
