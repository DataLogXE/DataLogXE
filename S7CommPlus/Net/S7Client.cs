#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 *
 * ISO-on-TCP connection request based on Snap7 (Sharp7.cs) by Davide Nardella (LGPL).
 * Replaces Net/S7Client.cs of S7CommPlusDriver by Thomas Wiens (LGPL-3.0-or-later).
 /****************************************************************************/
#endregion

using System.Net.Sockets;
using Org.BouncyCastle.Tls;
using Serilog;

namespace S7CommPlusDriver.Net
{
    /// <summary>
    /// TCP / ISO-on-TCP (TPKT + COTP) transport with TLS 1.3 tunnelled in COTP DT frames.
    /// One receive thread reads the socket without any idle timeout (a quiet PLC is not an error),
    /// feeds plain or decrypted bytes into the <see cref="FrameAssembler"/> and reports every
    /// failure through <see cref="Closed"/>; nothing is swallowed.
    /// </summary>
    internal sealed class S7Client : IDisposable
    {
        public const int DefaultPort = 102;
        private const int TpktHeaderSize = 4;
        private const int RequestedTpduSize = 1024;
        private const byte CotpDt = 0xF0;
        private const byte CotpCc = 0xD0;
        private const byte CotpDr = 0x80;
        private const byte CotpEr = 0x70;
        private const int MaxTpktLength = 65535;

        // CR: TPDU size 1024 (0x0a), calling TSAP 0x0100, called TSAP follows (length patched).
        private static readonly byte[] IsoConnectionRequestPrefix = {
            0x03, 0x00, 0x00, 0x00, 0x00, 0xe0, 0x00, 0x00, 0x00, 0x01, 0x00, 0xc0, 0x01, 0x0a,
            0xc1, 0x02, 0x01, 0x00, 0xc2, 0x00
        };

        private readonly ILogger _log;
        private readonly object _ioLock = new object();
        private readonly FrameAssembler _assembler;

        private Socket _socket;
        private Thread _receiveThread;
        private volatile bool _closed;
        private string _closeReason;

        private TlsClientProtocol _tls;
        private S7TlsClient _tlsClient;
        private volatile bool _tlsMode;
        private ManualResetEventSlim _handshakeDone;
        private Exception _handshakeError;
        private readonly byte[] _tlsReadBuf = new byte[16 * 1024 + 256];

        public int TpduSize { get; private set; } = RequestedTpduSize;
        public bool Connected => _socket != null && !_closed;
        public string CloseReason => _closeReason;

        /// <summary>Complete S7CommPlus PDU: [protoVersion, data...]. Called on the receive thread.</summary>
        public Action<byte[]> PduReceived;
        /// <summary>System event frame (protocol version 0xFE): [0xFE, data...]. Called on the receive thread.</summary>
        public Action<byte[]> SystemEventReceived;
        /// <summary>Connection closed or failed; argument is the reason. Raised once.</summary>
        public Action<string> Closed;

        public S7Client(ILogger log)
        {
            _log = log ?? Log.Logger;
            _assembler = new FrameAssembler(OnPdu, OnSystemEvent, m => _log.Warning("S7Client: {Message}", m));
        }

        /// <summary>TCP connect and COTP connection request / confirm; starts the receive thread.</summary>
        public int Connect(string host, int port, byte[] remoteTsap, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host) || remoteTsap == null || remoteTsap.Length == 0 || remoteTsap.Length > 32)
            {
                return S7Consts.errCliInvalidParams;
            }
            if (port <= 0) port = DefaultPort;

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.NoDelay = true;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                TrySetKeepAlive(socket);

                using (var cts = new CancellationTokenSource(timeoutMs))
                {
                    try
                    {
                        socket.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        _log.Warning("S7Client: TCP connect to {Host}:{Port} timed out after {TimeoutMs} ms", host, port, timeoutMs);
                        socket.Dispose();
                        return S7Consts.errTCPConnectionTimeout;
                    }
                }

                // COTP connection request
                byte[] cr = new byte[IsoConnectionRequestPrefix.Length + remoteTsap.Length];
                Buffer.BlockCopy(IsoConnectionRequestPrefix, 0, cr, 0, IsoConnectionRequestPrefix.Length);
                Buffer.BlockCopy(remoteTsap, 0, cr, IsoConnectionRequestPrefix.Length, remoteTsap.Length);
                cr[2] = (byte)(cr.Length >> 8);
                cr[3] = (byte)cr.Length;
                cr[4] = (byte)(cr.Length - 5);          // COTP length indicator
                cr[19] = (byte)remoteTsap.Length;
                socket.ReceiveTimeout = timeoutMs;
                socket.SendTimeout = timeoutMs;
                socket.Send(cr);

                byte[] cc = ReadTpkt(socket);
                if (cc == null || cc.Length < 7 || cc[5] != CotpCc)
                {
                    _log.Warning("S7Client: no COTP connection confirm from {Host}:{Port}", host, port);
                    socket.Dispose();
                    return S7Consts.errIsoConnect;
                }
                TpduSize = ParseTpduSize(cc);

                // From here on reads block without timeout: a quiet PLC is not an error.
                // Dead peers are detected by TCP keep-alive and the ping watchdog of the caller.
                socket.ReceiveTimeout = 0;
            }
            catch (SocketException ex)
            {
                _log.Warning("S7Client: connect to {Host}:{Port} failed: {Error}", host, port, ex.Message);
                socket.Dispose();
                return ex.SocketErrorCode == SocketError.TimedOut ? S7Consts.errTCPConnectionTimeout : S7Consts.errTCPConnectionFailed;
            }
            catch (Exception ex)
            {
                _log.Warning("S7Client: connect to {Host}:{Port} failed: {Error}", host, port, ex.Message);
                socket.Dispose();
                return S7Consts.errTCPConnectionFailed;
            }

            _socket = socket;
            _closed = false;
            _closeReason = null;
            _receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "S7CommPlus receive " + host };
            _receiveThread.Start();
            return 0;
        }

        /// <summary>
        /// Switches the connection to TLS 1.3 and runs the handshake. Must be called after the
        /// (plain) InitSsl response has been received and before anything else is sent.
        /// </summary>
        public int SslActivate(int timeoutMs)
        {
            if (!Connected) return S7Consts.errTCPNotConnected;
            _tlsClient = new S7TlsClient();
            _tls = new TlsClientProtocol();
            _handshakeDone = new ManualResetEventSlim(false);
            _handshakeError = null;
            try
            {
                lock (_ioLock)
                {
                    _tlsMode = true;
                    _tls.Connect(_tlsClient);
                    FlushTlsOutput();
                }
            }
            catch (Exception ex)
            {
                _log.Warning("S7Client: TLS start failed: {Error}", ex.Message);
                Fail("TLS start failed: " + ex.Message);
                return S7Consts.errCliTlsHandshake;
            }

            if (!_handshakeDone.Wait(timeoutMs))
            {
                Fail("TLS handshake timeout");
                return S7Consts.errCliTlsHandshake;
            }
            if (_handshakeError != null || _closed)
            {
                return S7Consts.errCliTlsHandshake;
            }
            return 0;
        }

        /// <summary>Keying material for the new legitimation (32 bytes, label "EXPERIMENTAL_OMS").</summary>
        public byte[] GetOMSExporterSecret()
        {
            try
            {
                return _tlsClient?.ExportKeyingMaterial("EXPERIMENTAL_OMS", 32);
            }
            catch (Exception ex)
            {
                _log.Warning("S7Client: keying material export failed: {Error}", ex.Message);
                return null;
            }
        }

        /// <summary>Sends one S7CommPlus frame (plain or TLS encrypted).</summary>
        public int Send(byte[] data, int count)
        {
            if (!Connected) return S7Consts.errTCPNotConnected;
            try
            {
                lock (_ioLock)
                {
                    if (_tlsMode)
                    {
                        _tls.WriteApplicationData(data, 0, count);
                        FlushTlsOutput();
                    }
                    else
                    {
                        SendCotp(data, 0, count);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Fail("send failed: " + ex.Message);
                return S7Consts.errTCPDataSend;
            }
        }

        public void Disconnect()
        {
            Fail("disconnect requested");
        }

        public void Dispose()
        {
            Disconnect();
        }

        #region receive

        private void ReceiveLoop()
        {
            var socket = _socket;
            try
            {
                while (!_closed)
                {
                    byte[] tpkt = ReadTpkt(socket);
                    if (tpkt == null)
                    {
                        Fail("connection closed by the PLC");
                        return;
                    }
                    if (tpkt.Length < 7)
                    {
                        continue;
                    }
                    byte type = (byte)(tpkt[5] & 0xF0);
                    if (type == CotpDr || type == CotpEr)
                    {
                        Fail(type == CotpDr ? "COTP disconnect request from the PLC" : "COTP error from the PLC");
                        return;
                    }
                    if (type != CotpDt)
                    {
                        continue;
                    }
                    int start = TpktHeaderSize + 1 + tpkt[4];
                    int len = tpkt.Length - start;
                    if (len <= 0) continue;

                    if (_tlsMode)
                    {
                        OfferTls(tpkt, start, len);
                    }
                    else
                    {
                        _assembler.Push(tpkt, start, len);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_closed)
                {
                    Fail(ex is TlsFatalAlert alert ? "TLS alert: " + alert.Message : "receive failed: " + ex.Message);
                }
            }
        }

        private void OfferTls(byte[] buf, int offset, int count)
        {
            int appBytes;
            lock (_ioLock)
            {
                if (_tls == null) return;
                bool wasHandshaking = _tls.IsHandshaking;
                try
                {
                    _tls.OfferInput(buf, offset, count);
                    FlushTlsOutput();
                }
                catch (Exception ex)
                {
                    if (wasHandshaking)
                    {
                        _handshakeError = ex;
                        _log.Warning("S7Client: TLS handshake failed: {Error}", ex.Message);
                        _handshakeDone?.Set();
                    }
                    throw;
                }
                if (wasHandshaking && !_tls.IsHandshaking)
                {
                    _handshakeDone?.Set();
                }
                appBytes = _tls.GetAvailableInputBytes();
            }
            while (appBytes > 0)
            {
                int n;
                lock (_ioLock)
                {
                    n = _tls.ReadInput(_tlsReadBuf, 0, Math.Min(appBytes, _tlsReadBuf.Length));
                    appBytes = _tls.GetAvailableInputBytes();
                }
                if (n <= 0) break;
                _assembler.Push(_tlsReadBuf, 0, n);
            }
        }

        private void OnPdu(byte[] pdu)
        {
            try
            {
                PduReceived?.Invoke(pdu);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "S7Client: PDU handler failed");
            }
        }

        private void OnSystemEvent(byte[] evt)
        {
            try
            {
                SystemEventReceived?.Invoke(evt);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "S7Client: system event handler failed");
            }
        }

        #endregion

        #region helpers

        // Caller holds _ioLock.
        private void FlushTlsOutput()
        {
            int pending = _tls.GetAvailableOutputBytes();
            if (pending <= 0) return;
            byte[] outBuf = new byte[pending];
            int n = _tls.ReadOutput(outBuf, 0, pending);
            SendCotp(outBuf, 0, n);
        }

        // Caller holds _ioLock. Splits into DT TPDUs (EOT only on the last one).
        private void SendCotp(byte[] data, int offset, int count)
        {
            var socket = _socket;
            if (socket == null) throw new IOException("not connected");
            int maxPayload = TpduSize - 7;
            do
            {
                int chunk = Math.Min(count, maxPayload);
                bool last = chunk == count;
                byte[] frame = new byte[7 + chunk];
                int total = frame.Length;
                frame[0] = 0x03;
                frame[1] = 0x00;
                frame[2] = (byte)(total >> 8);
                frame[3] = (byte)total;
                frame[4] = 0x02;
                frame[5] = CotpDt;
                frame[6] = (byte)(last ? 0x80 : 0x00);
                Buffer.BlockCopy(data, offset, frame, 7, chunk);
                int sent = 0;
                while (sent < frame.Length)
                {
                    sent += socket.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                }
                offset += chunk;
                count -= chunk;
            } while (count > 0);
        }

        /// <summary>Reads one TPKT packet; null on orderly close.</summary>
        private static byte[] ReadTpkt(Socket socket)
        {
            byte[] header = new byte[TpktHeaderSize];
            if (!ReadExactly(socket, header, 0, TpktHeaderSize)) return null;
            if (header[0] != 0x03)
            {
                throw new IOException($"invalid TPKT version 0x{header[0]:X2}");
            }
            int total = (header[2] << 8) | header[3];
            if (total < TpktHeaderSize + 2 || total > MaxTpktLength)
            {
                throw new IOException($"invalid TPKT length {total}");
            }
            byte[] packet = new byte[total];
            Buffer.BlockCopy(header, 0, packet, 0, TpktHeaderSize);
            if (!ReadExactly(socket, packet, TpktHeaderSize, total - TpktHeaderSize))
            {
                throw new IOException("connection closed inside a TPKT packet");
            }
            return packet;
        }

        private static bool ReadExactly(Socket socket, byte[] buf, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = socket.Receive(buf, offset + read, count - read, SocketFlags.None);
                if (n <= 0)
                {
                    if (read == 0 && offset == 0) return false;
                    throw new IOException("connection closed");
                }
                read += n;
            }
            return true;
        }

        private static int ParseTpduSize(byte[] cc)
        {
            // TPKT(4) LI(1) CC(1) dst-ref(2) src-ref(2) class(1) then parameters
            int end = Math.Min(cc.Length, TpktHeaderSize + 1 + cc[4]);
            int pos = TpktHeaderSize + 7;
            while (pos + 2 <= end)
            {
                byte code = cc[pos];
                int len = cc[pos + 1];
                if (pos + 2 + len > end) break;
                if (code == 0xC0 && len == 1)
                {
                    int exp = cc[pos + 2];
                    if (exp >= 7 && exp <= 13)
                    {
                        return Math.Min(RequestedTpduSize, 1 << exp);
                    }
                }
                pos += 2 + len;
            }
            return RequestedTpduSize;
        }

        private static void TrySetKeepAlive(Socket socket)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch
            {
                // Not supported on every platform; the ping watchdog still detects dead peers.
            }
        }

        private void Fail(string reason)
        {
            Socket socket;
            lock (_ioLock)
            {
                if (_closed) return;
                _closed = true;
                _closeReason = reason;
                socket = _socket;
                _tlsMode = false;
                try { _tls?.Close(); } catch { /* best effort */ }
            }
            try { socket?.Shutdown(SocketShutdown.Both); } catch { /* already gone */ }
            try { socket?.Dispose(); } catch { /* already gone */ }
            _handshakeDone?.Set();
            _log.Debug("S7Client: closed ({Reason})", reason);
            try
            {
                Closed?.Invoke(reason);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "S7Client: close handler failed");
            }
        }

        #endregion
    }
}
