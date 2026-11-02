#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 /****************************************************************************/
#endregion

using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace S7CommPlusDriver.Net
{
    /// <summary>
    /// TLS 1.3 client profile for S7CommPlus (managed, BouncyCastle).
    /// - TLS 1.3 only, AES-GCM suites only (fixed 16 byte AEAD tag, the fragment size calculation relies on it).
    /// - Key exchange group X25519 only: keeps the ClientHello around 250 bytes, the PLC silently drops
    ///   a ClientHello that does not fit into one 1024 byte TPDU.
    /// - The PLC certificate is not validated (self-signed device certificates).
    /// - Exposes the keying material exporter for the legitimation ("EXPERIMENTAL_OMS").
    /// </summary>
    internal sealed class S7TlsClient : DefaultTlsClient
    {
        public S7TlsClient() : base(new BcTlsCrypto(new SecureRandom()))
        {
        }

        protected override Org.BouncyCastle.Tls.ProtocolVersion[] GetSupportedVersions()
        {
            return Org.BouncyCastle.Tls.ProtocolVersion.TLSv13.Only();
        }

        protected override int[] GetSupportedCipherSuites()
        {
            return TlsUtilities.GetSupportedCipherSuites(Crypto, new[]
            {
                CipherSuite.TLS_AES_256_GCM_SHA384,
                CipherSuite.TLS_AES_128_GCM_SHA256,
            });
        }

        protected override IList<int> GetSupportedGroups(IList<int> namedGroupRoles)
        {
            return new List<int> { NamedGroup.x25519 };
        }

        public override TlsAuthentication GetAuthentication()
        {
            return new AcceptAnyServerAuthentication();
        }

        /// <summary>RFC 8446 exporter, as OpenSSL's SSL_export_keying_material without context.</summary>
        public byte[] ExportKeyingMaterial(string label, int length)
        {
            if (m_context == null)
            {
                return null;
            }
            return m_context.ExportKeyingMaterial(label, null, length);
        }

        private sealed class AcceptAnyServerAuthentication : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
            {
                // Device certificates of the PLC are self-signed: accepted like in the reference drivers.
            }

            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
            {
                return null;
            }
        }
    }
}
