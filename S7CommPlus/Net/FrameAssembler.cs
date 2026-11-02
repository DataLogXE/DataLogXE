#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 /****************************************************************************/
#endregion

namespace S7CommPlusDriver.Net
{
    /// <summary>
    /// Streaming reassembler for S7CommPlus wire frames: <c>0x72 ver lenHi lenLo data</c>.
    /// A logical PDU may span several frames and ends with a zero-length frame
    /// (<c>0x72 ver 00 00</c>). The input is a byte stream with arbitrary chunk
    /// boundaries (decrypted TLS data), so frames may be split or coalesced.
    /// Completed PDUs have the layout <c>[protoVersion, data of all fragments]</c>,
    /// which is what the response deserializers expect.
    /// </summary>
    internal sealed class FrameAssembler
    {
        public const int DefaultMaxPduBytes = 16 * 1024 * 1024;
        private const byte SyncByte = 0x72;

        private readonly Action<byte[]> _onPdu;
        private readonly Action<byte[]> _onSystemEvent;
        private readonly Action<string> _log;
        private readonly int _maxPduBytes;

        private byte[] _buf = new byte[4096];
        private int _bufLen;
        private MemoryStream _pdu;

        public long ResyncSkippedBytes { get; private set; }
        public int OversizedDropped { get; private set; }

        /// <param name="onPdu">Called with each complete PDU.</param>
        /// <param name="onSystemEvent">Called with each system event frame (protocol version 0xFE) as [0xFE, data].</param>
        public FrameAssembler(Action<byte[]> onPdu, Action<byte[]> onSystemEvent = null, Action<string> log = null, int maxPduBytes = DefaultMaxPduBytes)
        {
            _onPdu = onPdu ?? throw new ArgumentNullException(nameof(onPdu));
            _onSystemEvent = onSystemEvent;
            _log = log;
            _maxPduBytes = maxPduBytes > 0 ? maxPduBytes : DefaultMaxPduBytes;
        }

        public bool HasPartial => _pdu != null || _bufLen > 0;

        public void Reset()
        {
            _bufLen = 0;
            _pdu = null;
        }

        public void Push(byte[] chunk, int offset, int count)
        {
            if (count <= 0) return;
            if (_bufLen + count > _buf.Length)
            {
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _bufLen + count));
            }
            Buffer.BlockCopy(chunk, offset, _buf, _bufLen, count);
            _bufLen += count;

            int pos = 0;
            for (; ; )
            {
                if (pos < _bufLen && _buf[pos] != SyncByte)
                {
                    int idx = Array.IndexOf(_buf, SyncByte, pos, _bufLen - pos);
                    int skipped = (idx < 0 ? _bufLen : idx) - pos;
                    ResyncSkippedBytes += skipped;
                    _log?.Invoke($"frame resync: skipping {skipped} garbage bytes (partial PDU {(_pdu != null ? "dropped" : "none")})");
                    _pdu = null;
                    if (idx < 0)
                    {
                        pos = _bufLen;
                        break;
                    }
                    pos = idx;
                }

                if (_bufLen - pos < 4) break;

                byte protoVersion = _buf[pos + 1];
                int dataLen = (_buf[pos + 2] << 8) | _buf[pos + 3];

                if (dataLen == 0)
                {
                    pos += 4;
                    if (_pdu != null)
                    {
                        byte[] complete = _pdu.ToArray();
                        _pdu = null;
                        _onPdu(complete);
                    }
                    continue;
                }

                if (_bufLen - pos < 4 + dataLen) break;

                if (protoVersion == ProtocolVersion.SystemEvent)
                {
                    // System events have no trailer; they also invalidate a partial PDU.
                    _pdu = null;
                    if (_onSystemEvent != null)
                    {
                        byte[] evt = new byte[1 + dataLen];
                        evt[0] = protoVersion;
                        Buffer.BlockCopy(_buf, pos + 4, evt, 1, dataLen);
                        _onSystemEvent(evt);
                    }
                    pos += 4 + dataLen;
                    continue;
                }

                if (_pdu == null)
                {
                    _pdu = new MemoryStream();
                    _pdu.WriteByte(protoVersion);
                }
                _pdu.Write(_buf, pos + 4, dataLen);
                pos += 4 + dataLen;

                if (_pdu.Length > _maxPduBytes)
                {
                    OversizedDropped++;
                    _log?.Invoke($"oversized PDU dropped ({_pdu.Length} bytes)");
                    _pdu = null;
                }
            }

            if (pos >= _bufLen)
            {
                _bufLen = 0;
            }
            else if (pos > 0)
            {
                Buffer.BlockCopy(_buf, pos, _buf, 0, _bufLen - pos);
                _bufLen -= pos;
            }
            // Do not keep a large buffer alive after a big explore response.
            if (_bufLen == 0 && _buf.Length > 65536)
            {
                _buf = new byte[4096];
            }
        }
    }
}
