#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 /****************************************************************************/
#endregion

namespace S7CommPlusDriver.Symbols
{
    /// <summary>
    /// Converts the wire values of loggable types to a double: bool = 1/0, durations in ms,
    /// date and time (Date, DT, LDT, DTL) in milliseconds since 1970-01-01 (the PLC values taken as UTC).
    /// </summary>
    public static class ValueConverter
    {
        private static readonly DateTime S7DateBase = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static bool TryToDouble(PValue value, uint softdatatype, out double result)
        {
            result = 0;
            if (SymbolBrowser.IsDateType(softdatatype))
            {
                return TryDateToUnixMs(value, softdatatype, out result);
            }
            switch (value)
            {
                case ValueBool v: result = v.GetValue() ? 1 : 0; return true;
                case ValueUSInt v: result = v.GetValue(); return true;
                case ValueUInt v:
                    result = v.GetValue();
                    return true;
                case ValueUDInt v: result = v.GetValue(); break;
                case ValueULInt v: result = v.GetValue(); break;
                case ValueSInt v: result = v.GetValue(); return true;
                case ValueInt v: result = v.GetValue(); return true;
                case ValueDInt v: result = v.GetValue(); break;
                case ValueLInt v: result = v.GetValue(); break;
                case ValueByte v: result = v.GetValue(); return true;
                case ValueWord v:
                    if (softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_S5TIME)
                    {
                        return TryDecodeS5Time(v.GetValue(), out result);
                    }
                    result = v.GetValue();
                    return true;
                case ValueDWord v: result = v.GetValue(); return true;
                case ValueLWord v: result = v.GetValue(); return true;
                case ValueReal v: result = v.GetValue(); return !float.IsNaN(v.GetValue()) && !float.IsInfinity(v.GetValue());
                case ValueLReal v: result = v.GetValue(); return !double.IsNaN(v.GetValue()) && !double.IsInfinity(v.GetValue());
                case ValueTimespan v: result = v.GetValue(); break;
                default:
                    return false;
            }
            // Time types: TIME/TOD are ms already; LTIME/LTOD are ns.
            if (softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_LTIME || softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_LTOD)
            {
                result /= 1_000_000.0;
            }
            return true;
        }

        private static bool TryDateToUnixMs(PValue value, uint softdatatype, out double ms)
        {
            ms = 0;
            try
            {
                switch (value)
                {
                    case ValueUInt v when softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DATE:
                        // days since 1990-01-01
                        ms = ToUnixMs(S7DateBase.AddDays(v.GetValue()));
                        return true;
                    case ValueTimestamp v:
                        // LDT: nanoseconds since 1970-01-01
                        ulong ns = v.GetValue();
                        ms = (double)(ns / 1_000_000UL) + (ns % 1_000_000UL) / 1_000_000.0;
                        return true;
                    case ValueUSIntArray v when softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DATEANDTIME:
                        return TryDecodeDateAndTime(v.GetValue(), out ms);
                    case ValueStruct v when softdatatype == Softdatatype.S7COMMP_SOFTDATATYPE_DTL:
                        return v.GetStructElement(0x02000043) is ValueByteArray b && TryDecodeDtl(b.GetValue(), out ms);
                    default:
                        return false;
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        /// <summary>DATE_AND_TIME: 8 BCD bytes year(90..89 = 1990..2089), month, day, hour, minute, second, ms (3 digits) + weekday.</summary>
        public static bool TryDecodeDateAndTime(byte[] b, out double ms)
        {
            ms = 0;
            if (b == null || b.Length < 8) return false;
            int Bcd(byte x) => (x >> 4) * 10 + (x & 0x0F);
            for (int i = 0; i < 7; i++)
            {
                if ((b[i] >> 4) > 9 || (b[i] & 0x0F) > 9) return false;
            }
            int yy = Bcd(b[0]);
            int year = yy >= 90 ? 1900 + yy : 2000 + yy;
            int month = Bcd(b[1]), day = Bcd(b[2]), hour = Bcd(b[3]), minute = Bcd(b[4]), second = Bcd(b[5]);
            int millis = Bcd(b[6]) * 10 + (b[7] >> 4);
            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) return false;
            ms = ToUnixMs(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc)) + millis;
            return true;
        }

        /// <summary>DTL: year (u16 BE), month, day, weekday, hour, minute, second, nanoseconds (u32 BE).</summary>
        public static bool TryDecodeDtl(byte[] b, out double ms)
        {
            ms = 0;
            if (b == null || b.Length < 12) return false;
            int year = (b[0] << 8) | b[1];
            int month = b[2], day = b[3], hour = b[5], minute = b[6], second = b[7];
            uint nanos = ((uint)b[8] << 24) | ((uint)b[9] << 16) | ((uint)b[10] << 8) | b[11];
            if (year < 1970 || year > 2554 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59) return false;
            ms = ToUnixMs(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc)) + nanos / 1_000_000.0;
            return true;
        }

        private static double ToUnixMs(DateTime utc) => (utc - DateTime.UnixEpoch).TotalMilliseconds;

        /// <summary>S5TIME: bits 12-13 time base (10 ms, 100 ms, 1 s, 10 s), bits 0-11 three BCD digits.</summary>
        public static bool TryDecodeS5Time(ushort raw, out double ms)
        {
            ms = 0;
            int d0 = raw & 0xF, d1 = (raw >> 4) & 0xF, d2 = (raw >> 8) & 0xF;
            if (d0 > 9 || d1 > 9 || d2 > 9) return false;
            int count = d2 * 100 + d1 * 10 + d0;
            int baseMs;
            switch ((raw >> 12) & 0x3)
            {
                case 0: baseMs = 10; break;
                case 1: baseMs = 100; break;
                case 2: baseMs = 1000; break;
                default: baseMs = 10000; break;
            }
            ms = count * baseMs;
            return true;
        }
    }
}
