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
using DataLogService.DataSource.OpcUa;
using Opc.Ua;
using OpcUaSimpleClientApi;
using Xunit;

namespace DataLogLib.Tests
{
    public class OpcUaSecurityTests
    {
        private const string Url = "opc.tcp://192.0.2.10:4840";
        private const string Thumbprint = "06A82613A3FD7AC5B788096701C8DA9D3C3D389B";

        private static EndpointDescription Endpoint(MessageSecurityMode mode, string policyUri, byte level,
            string url = "opc.tcp://plc-hostname:4840/path")
        {
            return new EndpointDescription
            {
                EndpointUrl = url,
                SecurityMode = mode,
                SecurityPolicyUri = policyUri,
                SecurityLevel = level,
                TransportProfileUri = Profiles.UaTcpTransport
            };
        }

        private static readonly EndpointDescription[] Offered =
        [
            Endpoint(MessageSecurityMode.None, SecurityPolicies.None, 0),
            Endpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256, 5),
            Endpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256, 10),
            Endpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Aes128_Sha256_RsaOaep, 20),
            Endpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic128Rsa15, 30)
        ];

        private static OpcUaSecuritySettings Settings(string mode, string policy = OpcUaSecurityValues.PolicyBest,
            string trust = OpcUaSecurityValues.TrustPinned, string thumbprint = Thumbprint)
        {
            return new OpcUaSecuritySettings(mode, policy, trust, thumbprint);
        }

        [Fact]
        public void Best_takes_the_strongest_policy_of_the_mode_not_the_highest_level()
        {
            var chosen = OpcUaEndpointSelector.Select(Offered, Settings(OpcUaSecurityValues.ModeSignAndEncrypt), Url);

            Assert.Equal(MessageSecurityMode.SignAndEncrypt, chosen.SecurityMode);
            Assert.Equal(SecurityPolicies.Basic256Sha256, chosen.SecurityPolicyUri);
        }

        [Fact]
        public void A_named_policy_is_taken_exactly()
        {
            var chosen = OpcUaEndpointSelector.Select(
                Offered, Settings(OpcUaSecurityValues.ModeSignAndEncrypt, "Aes128_Sha256_RsaOaep"), Url);

            Assert.Equal(SecurityPolicies.Aes128_Sha256_RsaOaep, chosen.SecurityPolicyUri);
        }

        [Fact]
        public void No_matching_endpoint_names_the_offered_ones()
        {
            var ex = Assert.Throws<ServiceResultException>(() => OpcUaEndpointSelector.Select(
                Offered, Settings(OpcUaSecurityValues.ModeSign, "Aes256_Sha256_RsaPss"), Url));

            Assert.Equal(StatusCodes.BadSecurityPolicyRejected, ex.StatusCode);
            Assert.Contains("Sign Basic256Sha256", ex.Message);
            Assert.Contains("SignAndEncrypt Aes128_Sha256_RsaOaep", ex.Message);
        }

        [Fact]
        public void The_endpoint_gets_the_address_the_user_entered_and_keeps_its_path()
        {
            var chosen = OpcUaEndpointSelector.Select(Offered, Settings(OpcUaSecurityValues.ModeSign), Url);

            Assert.Equal("opc.tcp://192.0.2.10:4840/path", chosen.EndpointUrl);
            Assert.Equal("opc.tcp://plc-hostname:4840/path", Offered[1].EndpointUrl);
        }

        [Fact]
        public void An_unsecured_source_keeps_the_endpoint_without_security_when_the_server_offers_one()
        {
            Assert.Null(OpcUaEndpointSelector.SelectInsteadOfUnsecured(Offered, Url));
            Assert.Null(OpcUaEndpointSelector.SelectInsteadOfUnsecured([], Url));
        }

        [Fact]
        public void An_unsecured_source_takes_the_best_secured_endpoint_of_a_server_without_one()
        {
            // A SIMATIC S7-1500 offers SignAndEncrypt only
            EndpointDescription[] simatic =
            [
                Endpoint(MessageSecurityMode.Sign, SecurityPolicies.Basic256Sha256, 50),
                Endpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic256Sha256, 115),
                Endpoint(MessageSecurityMode.SignAndEncrypt, SecurityPolicies.Basic128Rsa15, 130)
            ];

            var chosen = OpcUaEndpointSelector.SelectInsteadOfUnsecured(simatic, Url);

            Assert.Equal(MessageSecurityMode.SignAndEncrypt, chosen.SecurityMode);
            Assert.Equal(SecurityPolicies.Basic256Sha256, chosen.SecurityPolicyUri);
            Assert.Equal("opc.tcp://192.0.2.10:4840/path", chosen.EndpointUrl);
        }

        [Fact]
        public void An_endpoint_without_path_takes_the_entered_address_as_it_is()
        {
            Assert.Equal(Url, OpcUaEndpointSelector.WithAddressOf("opc.tcp://other:4840", Url));
        }

        [Fact]
        public void Sources_before_0_0_200_are_unsecured_and_accept_any_certificate()
        {
            var settings = OpcUaSecuritySettings.FromModel(new DataSourceModel());

            Assert.True(settings.IsUnsecured);
            Assert.True(settings.AcceptsAnyCertificate);
            Assert.Equal(MessageSecurityMode.None, settings.MessageSecurityMode);
        }

        [Fact]
        public void Unknown_stored_values_fall_back_to_the_old_behaviour()
        {
            var settings = new OpcUaSecuritySettings("bogus", "", "bogus", "zz");

            Assert.True(settings.IsUnsecured);
            Assert.Equal(OpcUaSecurityValues.PolicyBest, settings.Policy);
            Assert.Equal(OpcUaSecurityValues.TrustAny, settings.Trust);
            Assert.Equal("", settings.PinnedThumbprint);
        }

        [Fact]
        public void A_pinned_guard_accepts_only_the_pinned_thumbprint_in_any_spelling()
        {
            var guard = new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeSignAndEncrypt));

            Assert.False(guard.AcceptsAny);
            Assert.True(guard.Accepts(Thumbprint.ToLowerInvariant()));
            Assert.True(guard.Accepts("06:A8:26:13:A3:FD:7A:C5:B7:88:09:67:01:C8:DA:9D:3C:3D:38:9B"));
            Assert.False(guard.Accepts("1111111111111111111111111111111111111111"));
            Assert.False(guard.Accepts(""));
        }

        [Fact]
        public void A_pinned_guard_without_thumbprint_accepts_nothing()
        {
            var guard = new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeSign, thumbprint: ""));

            Assert.False(guard.Accepts(Thumbprint));
        }

        [Fact]
        public void Unsecured_and_any_accept_every_certificate()
        {
            Assert.True(new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeNone)).Accepts("00"));
            Assert.True(new OpcUaCertificateGuard(
                Settings(OpcUaSecurityValues.ModeSign, trust: OpcUaSecurityValues.TrustAny)).Accepts("00"));
        }

        [Fact]
        public void A_secured_channel_closed_by_the_server_hints_at_the_client_certificate()
        {
            var guard = new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeSignAndEncrypt));
            var ex = new ServiceResultException(StatusCodes.BadSecureChannelClosed);

            Assert.Contains("client certificate of DataLogXE", guard.Describe(ex));
            Assert.True(guard.IsSecurityFailure(ex));
        }

        [Fact]
        public void An_unsecured_failure_keeps_its_own_text()
        {
            var guard = new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeNone));
            var ex = new ServiceResultException(StatusCodes.BadSecureChannelClosed, "Closed by the server");

            Assert.Equal("Closed by the server", guard.Describe(ex));
            Assert.False(guard.IsSecurityFailure(ex));
        }

        private static OpcUaConnectionTestResult Shown(string thumbprint)
        {
            return new OpcUaConnectionTestResult
            {
                ServerCertificate = new OpcUaCertificateInfo { Thumbprint = thumbprint }
            };
        }

        private static readonly EndpointDescription Chosen = Offered[3];

        [Fact]
        public void A_test_without_a_pin_asks_to_trust_the_certificate_before_it_signs_in()
        {
            var result = Shown(Thumbprint);
            var asks = OpcUaClientBase.NeedsDecision(result, Offered, Chosen,
                Settings(OpcUaSecurityValues.ModeSignAndEncrypt, thumbprint: ""));

            Assert.True(asks);
            Assert.Equal(OpcUaCertificateDecision.New, result.CertificateDecision);
            Assert.False(result.Success);
        }

        [Fact]
        public void A_test_against_another_certificate_asks_before_it_signs_in()
        {
            var result = Shown("1111111111111111111111111111111111111111");
            var asks = OpcUaClientBase.NeedsDecision(result, Offered, Chosen, Settings(OpcUaSecurityValues.ModeSignAndEncrypt));

            Assert.True(asks);
            Assert.Equal(OpcUaCertificateDecision.Changed, result.CertificateDecision);
            Assert.Contains(Thumbprint, result.Error);
        }

        [Fact]
        public void The_pinned_certificate_in_any_spelling_goes_on_to_the_sign_in()
        {
            var result = Shown(Thumbprint.ToLowerInvariant());

            Assert.False(OpcUaClientBase.NeedsDecision(result, Offered, Chosen, Settings(OpcUaSecurityValues.ModeSignAndEncrypt)));
            Assert.Null(result.CertificateDecision);
        }

        [Fact]
        public void Accept_any_and_unsecured_tests_ask_nothing_about_the_certificate()
        {
            var other = "1111111111111111111111111111111111111111";

            Assert.False(OpcUaClientBase.NeedsDecision(Shown(other), Offered, Chosen,
                Settings(OpcUaSecurityValues.ModeSignAndEncrypt, trust: OpcUaSecurityValues.TrustAny)));
            Assert.False(OpcUaClientBase.NeedsDecision(Shown(other), Offered, null, Settings(OpcUaSecurityValues.ModeNone)));
        }

        [Fact]
        public void A_secured_test_against_a_server_without_security_asks_to_connect_unsecured()
        {
            var result = Shown(Thumbprint);
            EndpointDescription[] unsecuredOnly = [Endpoint(MessageSecurityMode.None, SecurityPolicies.None, 0)];

            Assert.True(OpcUaClientBase.NeedsDecision(result, unsecuredOnly, null, Settings(OpcUaSecurityValues.ModeSignAndEncrypt)));
            Assert.Equal(OpcUaCertificateDecision.UnsecuredOnly, result.CertificateDecision);
        }

        [Fact]
        public void Without_a_fitting_endpoint_the_session_attempt_reports_the_offered_ones()
        {
            var result = Shown("1111111111111111111111111111111111111111");

            Assert.False(OpcUaClientBase.NeedsDecision(result, Offered, null, Settings(OpcUaSecurityValues.ModeSignAndEncrypt)));
            Assert.Null(result.CertificateDecision);
        }

        [Fact]
        public void A_closed_channel_is_a_client_trust_failure_but_a_wrong_user_is_not()
        {
            var guard = new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeSignAndEncrypt));

            Assert.True(guard.IsClientTrustFailure(new ServiceResultException(StatusCodes.BadSecureChannelClosed)));
            Assert.False(guard.IsClientTrustFailure(new ServiceResultException(StatusCodes.BadIdentityTokenRejected)));
            Assert.False(new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeNone))
                .IsClientTrustFailure(new ServiceResultException(StatusCodes.BadSecureChannelClosed)));
        }

        [Fact]
        public void A_refused_anonymous_session_asks_for_a_user_not_for_the_client_certificate()
        {
            var guard = new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeSignAndEncrypt));
            var ex = new ServiceResultException(StatusCodes.BadIdentityTokenRejected,
                "Endpoint does not support the user identity type provided.");

            var text = guard.Describe(ex);

            Assert.Contains("Sign in with a user", text);
            Assert.DoesNotContain("client certificate", text);
            Assert.False(guard.IsClientTrustFailure(ex));
            Assert.True(guard.IsSecurityFailure(ex));
        }

        [Fact]
        public void A_refused_user_names_the_user_name_or_password()
        {
            var guard = new OpcUaCertificateGuard(Settings(OpcUaSecurityValues.ModeNone));
            var ex = new ServiceResultException(StatusCodes.BadUserAccessDenied);

            Assert.Equal("The server refused the user name or password.", guard.Describe(ex, signedIn: true));
            Assert.True(OpcUaCertificateGuard.IsSignInFailure(ex));
        }

        [Fact]
        public void Thumbprints_are_stored_as_upper_case_hex()
        {
            Assert.Equal(Thumbprint, OpcUaSecurityValues.NormalizeThumbprint(" 06:a8:26:13:a3:fd:7a:c5:b7:88:09:67:01:c8:da:9d:3c:3d:38:9b "));
            Assert.Equal("", OpcUaSecurityValues.NormalizeThumbprint(null));
        }
    }
}
