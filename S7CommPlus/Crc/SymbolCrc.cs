#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 * The CRC algorithm was informed by HarpoS7
 * (https://github.com/bonk-dev/HarpoS7, Copyright (c) 2024 bonk, MIT license).
 /****************************************************************************/
#endregion

using System.Text;

namespace S7CommPlusDriver.Crc
{
    /// <summary>CRC32 used by S7CommPlus: polynomial 0xF4ACFB13, MSB first, init 0, no final XOR.</summary>
    public struct S7Crc32
    {
        public const uint Polynomial = 0xF4ACFB13;
        private static readonly uint[] Table = BuildTable();
        private uint _state;

        public uint Result => _state;

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i << 24;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ Polynomial : crc << 1;
                }
                t[i] = crc;
            }
            return t;
        }

        public void Update(byte b)
        {
            _state = Table[(b ^ (_state >> 24)) & 0xFF] ^ (_state << 8);
        }

        public void Update(ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) Update(b);
        }

        /// <summary>Member names are hashed as UTF-8, as the PLC stores them.</summary>
        public void Update(string s)
        {
            Update(Encoding.UTF8.GetBytes(s));
        }

        public void UpdateUInt32LE(uint v)
        {
            Update((byte)v);
            Update((byte)(v >> 8));
            Update((byte)(v >> 16));
            Update((byte)(v >> 24));
        }

        public void UpdateInt32LE(int v) => UpdateUInt32LE(unchecked((uint)v));
    }

    /// <summary>One member on the way from the block to the accessed variable.</summary>
    public readonly struct CrcSegment
    {
        public readonly string Name;
        public readonly uint Softdatatype;
        public readonly bool IsArray;
        public readonly int LowerBound;
        /// <summary>Inner CRC from the type info (VartypeList element); 0 = compute it from name and type.</summary>
        public readonly uint Inner;

        public CrcSegment(string name, uint softdatatype, bool isArray = false, int lowerBound = 0, uint inner = 0)
        {
            Name = name;
            Softdatatype = softdatatype;
            IsArray = isArray;
            LowerBound = lowerBound;
            Inner = inner;
        }
    }

    /// <summary>
    /// SymbolCRC of an ItemAddress. The PLC compares it with the symbol behind the LIDs and rejects
    /// the access when the program was changed, so stale LID paths can never read another variable.
    /// <para>inner(member) = CRC(utf8(name), typeinfo); typeinfo = TypeCode, or 0x10 elementTypeCode lowerBound(i32 LE) for arrays.</para>
    /// <para>The type info carries inner(member) for every member; it is used when present, because the computed
    /// form is only right for scalars and one-dimensional arrays (multi-dimensional arrays use an unknown form).</para>
    /// <para>result = CRC(u32le(inner1) [0x89 u32le(inner2)] ...). The block name and array indexes are not part of it.</para>
    /// </summary>
    public static class SymbolCrc
    {
        public const byte StructChildDelimiter = 0x89;

        public static class TypeCode
        {
            public const byte Bool = 0x01, Byte = 0x02, Char = 0x03, Word = 0x04, Int = 0x05, DWord = 0x06, DInt = 0x07, Real = 0x08,
                Date = 0x09, TimeOfDay = 0x0A, Time = 0x0B, S5Time = 0x0C, DateAndTime = 0x0E, Array = 0x10, Struct = 0x11, String = 0x13,
                LReal = 0x30, ULInt = 0x31, LInt = 0x32, LWord = 0x33, USInt = 0x34, UInt = 0x35, UDInt = 0x36, SInt = 0x37,
                WChar = 0x3D, WString = 0x3E, LTime = 0x40, LTimeOfDay = 0x41;
        }

        /// <summary>Softdatatype of the type info → CRC TypeCode (most are identical).</summary>
        public static byte SoftdatatypeToTypeCode(uint sd)
        {
            switch (sd)
            {
                case 40: return TypeCode.Bool;          // BBOOL
                // 32-bit system types
                case 128: case 129: case 130: case 131: case 132: case 133: case 134: case 171:
                    return TypeCode.DWord;
                // 16-bit Word system types (HW_*, CONN_*)
                case 144: case 145: case 146: case 147: case 148: case 149: case 150: case 151:
                case 152: case 153: case 154: case 155: case 156: case 168: case 169: case 170:
                    return TypeCode.Word;
                // OB numbers
                case 160: case 161: case 162: case 163: case 164: case 192: case 193: case 195: case 196: case 197:
                    return TypeCode.Int;
                // DB_ANY, PORT, RTM, PIP
                case 173: case 174: case 175: case 208: case 209: case 210:
                    return TypeCode.UInt;
                default:
                    return (byte)sd;
            }
        }

        public static uint MemberInnerCrc(in CrcSegment seg)
        {
            if (seg.Inner != 0) return seg.Inner;
            var crc = new S7Crc32();
            crc.Update(seg.Name ?? "");
            if (seg.IsArray)
            {
                crc.Update(TypeCode.Array);
                crc.Update(SoftdatatypeToTypeCode(seg.Softdatatype));
                crc.UpdateInt32LE(seg.LowerBound);
            }
            else
            {
                crc.Update(SoftdatatypeToTypeCode(seg.Softdatatype));
            }
            return crc.Result;
        }

        public static uint Compute(IReadOnlyList<CrcSegment> segments)
        {
            if (segments == null || segments.Count == 0) return 0;
            var crc = new S7Crc32();
            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0) crc.Update(StructChildDelimiter);
                crc.UpdateUInt32LE(MemberInnerCrc(segments[i]));
            }
            return crc.Result;
        }
    }
}
