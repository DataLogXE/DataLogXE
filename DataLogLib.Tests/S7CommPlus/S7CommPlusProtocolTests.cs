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

using System.Text;
using S7CommPlusDriver;
using S7CommPlusDriver.Crc;
using S7CommPlusDriver.Net;
using S7CommPlusDriver.Symbols;
using Xunit;

namespace DataLogLib.Tests.S7CommPlus
{
    public class S7CommPlusCrcTests
    {
        private static uint Crc(params byte[] data)
        {
            var c = new S7Crc32();
            c.Update(data);
            return c.Result;
        }

        [Fact]
        public void Table_entry_one()
        {
            Assert.Equal(4104977171u, Crc(1));
        }

        [Fact]
        public void Name_and_type_inner_crc()
        {
            var c = new S7Crc32();
            c.Update("Test2");
            c.Update(0x07);
            Assert.Equal(0x9E858004u, c.Result);
            Assert.Equal(0x9E858004u, SymbolCrc.MemberInnerCrc(new CrcSegment("Test2", Softdatatype.S7COMMP_SOFTDATATYPE_DINT)));
        }

        [Theory]
        [InlineData("Test2", 7u, false, 0, 0x4CA70840u)]
        [InlineData("TEST 3", 1u, true, 0, 0x0CC865FBu)]
        [InlineData("readings", 8u, true, 0, 0x6580D02Cu)]
        [InlineData("flag", 1u, false, 0, 0xFC8BA389u)]
        public void Item_crc_of_a_db_member(string name, uint softdatatype, bool isArray, int lowerBound, uint expected)
        {
            Assert.Equal(expected, SymbolCrc.Compute([new CrcSegment(name, softdatatype, isArray, lowerBound)]));
        }

        [Fact]
        public void Item_crc_of_a_struct_member()
        {
            var crc = SymbolCrc.Compute(
            [
                new CrcSegment("TEST 4", Softdatatype.S7COMMP_SOFTDATATYPE_STRUCT),
                new CrcSegment("T4ChildBool", Softdatatype.S7COMMP_SOFTDATATYPE_BOOL),
            ]);
            Assert.Equal(0x1AC12998u, crc);
        }

        [Fact]
        public void Inner_crc_from_the_type_info_is_used()
        {
            // x_Int : Array[0..1, -1..0, 1..2] of Int (BT_O_Arrays3D on the 1512SP): the PLC's member CRC 6C2C503C
            // differs from the computed one-dimensional form and is the one the PLC accepts.
            var seg = new CrcSegment("x_Int", Softdatatype.S7COMMP_SOFTDATATYPE_INT, true, 1, 0x6C2C503Cu);
            Assert.Equal(0x6C2C503Cu, SymbolCrc.MemberInnerCrc(seg));
            var outer = new S7Crc32();
            outer.UpdateUInt32LE(0x6C2C503Cu);
            Assert.Equal(outer.Result, SymbolCrc.Compute([seg]));
            // Without it the CRC is computed: ba7 : Array[-3..13] of Bool, PLC member CRC 19C87EA5.
            Assert.Equal(0x19C87EA5u, SymbolCrc.MemberInnerCrc(new CrcSegment("ba7", Softdatatype.S7COMMP_SOFTDATATYPE_BOOL, true, -3)));
        }

        [Fact]
        public void Names_are_hashed_as_utf8()
        {
            var utf8 = SymbolCrc.Compute([new CrcSegment("wärt", Softdatatype.S7COMMP_SOFTDATATYPE_REAL)]);
            var inner = new S7Crc32();
            inner.Update(Encoding.Latin1.GetBytes("wärt"));
            inner.Update(0x08);
            var outer = new S7Crc32();
            outer.UpdateUInt32LE(inner.Result);
            Assert.NotEqual(outer.Result, utf8);
        }

        [Theory]
        [InlineData(40u, 0x01)]
        [InlineData(8u, 0x08)]
        [InlineData(144u, 0x04)]
        [InlineData(208u, 0x35)]
        [InlineData(67u, 67)]
        public void Softdatatype_to_type_code(uint softdatatype, int expected)
        {
            Assert.Equal((byte)expected, SymbolCrc.SoftdatatypeToTypeCode(softdatatype));
        }
    }

    public class S7CommPlusFrameAssemblerTests
    {
        private static byte[] Frame(byte ver, params byte[] data)
        {
            var b = new byte[4 + data.Length];
            b[0] = 0x72;
            b[1] = ver;
            b[2] = (byte)(data.Length >> 8);
            b[3] = (byte)data.Length;
            data.CopyTo(b, 4);
            return b;
        }

        private static byte[] Trailer(byte ver) => [0x72, ver, 0, 0];

        [Fact]
        public void Fragments_split_across_chunks_give_one_pdu()
        {
            var pdus = new List<byte[]>();
            var asm = new FrameAssembler(pdus.Add);
            var stream = Frame(2, 1, 2, 3).Concat(Frame(2, 4, 5)).Concat(Trailer(2)).ToArray();
            foreach (var b in stream)
            {
                asm.Push([b], 0, 1);
            }
            Assert.Single(pdus);
            Assert.Equal(new byte[] { 2, 1, 2, 3, 4, 5 }, pdus[0]);
            Assert.False(asm.HasPartial);
        }

        [Fact]
        public void Coalesced_pdus_are_separated()
        {
            var pdus = new List<byte[]>();
            var asm = new FrameAssembler(pdus.Add);
            var stream = Frame(2, 9).Concat(Trailer(2)).Concat(Frame(2, 8, 7)).Concat(Trailer(2)).ToArray();
            asm.Push(stream, 0, stream.Length);
            Assert.Equal(2, pdus.Count);
            Assert.Equal(new byte[] { 2, 8, 7 }, pdus[1]);
        }

        [Fact]
        public void Garbage_is_skipped_and_the_partial_pdu_dropped()
        {
            var pdus = new List<byte[]>();
            var asm = new FrameAssembler(pdus.Add);
            var stream = Frame(2, 1).Concat(new byte[] { 0x00, 0x11 }).Concat(Frame(2, 5)).Concat(Trailer(2)).ToArray();
            asm.Push(stream, 0, stream.Length);
            Assert.Single(pdus);
            Assert.Equal(new byte[] { 2, 5 }, pdus[0]);
            Assert.True(asm.ResyncSkippedBytes > 0);
        }

        [Fact]
        public void System_events_are_reported_separately()
        {
            var pdus = new List<byte[]>();
            var events = new List<byte[]>();
            var asm = new FrameAssembler(pdus.Add, events.Add);
            var stream = Frame(0xFE, 1, 2).Concat(Frame(2, 3)).Concat(Trailer(2)).ToArray();
            asm.Push(stream, 0, stream.Length);
            Assert.Single(events);
            Assert.Equal(new byte[] { 0xFE, 1, 2 }, events[0]);
            Assert.Single(pdus);
        }

        [Fact]
        public void Oversized_pdu_is_dropped()
        {
            var pdus = new List<byte[]>();
            var asm = new FrameAssembler(pdus.Add, maxPduBytes: 4);
            var stream = Frame(2, 1, 2, 3, 4, 5).Concat(Trailer(2)).ToArray();
            asm.Push(stream, 0, stream.Length);
            Assert.Empty(pdus);
            Assert.Equal(1, asm.OversizedDropped);
        }
    }

    public class S7CommPlusSymbolPathTests
    {
        [Fact]
        public void Parses_quoted_names_indexes_and_members()
        {
            var t = SymbolPath.Parse("\"DB Test\".Motor[3].\"Speed set\"[1,2]");
            Assert.Equal(3, t.Count);
            Assert.Equal("DB Test", t[0].Name);
            Assert.Equal("Motor", t[1].Name);
            Assert.Equal(new[] { 3 }, t[1].Indexes);
            Assert.Equal("Speed set", t[2].Name);
            Assert.Equal(new[] { 1, 2 }, t[2].Indexes);
        }

        [Theory]
        [InlineData("DB_Test.Value", "\"DB_Test\".Value")]
        [InlineData("\"DB_Test\".a.b[-1]", "\"DB_Test\".a.b[-1]")]
        [InlineData("\"Tag 1\"", "\"Tag 1\"")]
        [InlineData("DB1.\"x y\"", "\"DB1\".\"x y\"")]
        public void Normalizes(string input, string expected)
        {
            Assert.Equal(expected, SymbolPath.Normalize(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData("\"DB")]
        [InlineData("DB.")]
        [InlineData("DB.a[x]")]
        [InlineData("DB.a[1")]
        public void Rejects_invalid_paths(string input)
        {
            Assert.Throws<FormatException>(() => SymbolPath.Parse(input));
        }
    }

    public class S7CommPlusArrayIndexTests
    {
        private static PVartypeListElement Array1(int lower, uint count)
        {
            return new PVartypeListElement
            {
                Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_INT,
                OffsetInfoType = new POffsetInfoType_Array1Dim { ArrayLowerBounds = lower, ArrayElementCount = count },
            };
        }

        private static PVartypeListElement ArrayM(uint softdatatype, int[] lbs, uint[] counts)
        {
            var oi = new POffsetInfoType_ArrayMDim { ArrayLowerBounds = lbs[0], ArrayElementCount = counts.Aggregate(1u, (a, c) => a * c) };
            for (int i = 0; i < lbs.Length; i++)
            {
                oi.MdimArrayLowerBounds[i] = lbs[i];
                oi.MdimArrayElementCount[i] = counts[i];
            }
            return new PVartypeListElement { Softdatatype = softdatatype, OffsetInfoType = oi };
        }

        [Fact]
        public void One_dimension_uses_the_offset_from_the_lower_bound()
        {
            Assert.True(SymbolBrowser.TryGetArrayElementId(Array1(-5, 10), [-3], out uint id, out int lb, out _));
            Assert.Equal(2u, id);
            Assert.Equal(-5, lb);
            Assert.False(SymbolBrowser.TryGetArrayElementId(Array1(-5, 10), [5], out _, out _, out string error));
            Assert.Contains("out of range", error);
        }

        [Fact]
        public void Two_dimensions_last_index_varies_fastest()
        {
            // Array[1..2, 0..2]: dimension 0 is the right one (3 elements).
            var vte = ArrayM(Softdatatype.S7COMMP_SOFTDATATYPE_INT, [0, 1], [3, 2]);
            Assert.True(SymbolBrowser.TryGetArrayElementId(vte, [1, 0], out uint a, out _, out _));
            Assert.True(SymbolBrowser.TryGetArrayElementId(vte, [2, 2], out uint b, out _, out _));
            Assert.Equal(0u, a);
            Assert.Equal(5u, b);
        }

        [Fact]
        public void Bit_bool_rows_are_padded_to_bytes()
        {
            // Array[0..1, 0..2] of Bool in an optimized block: rows of 3 bits use 8 ids.
            var vte = ArrayM(Softdatatype.S7COMMP_SOFTDATATYPE_BBOOL, [0, 0], [3, 2]);
            Assert.True(SymbolBrowser.TryGetArrayElementId(vte, [1, 0], out uint id, out _, out _));
            Assert.Equal(8u, id);
        }

        [Fact]
        public void Bool_rows_in_standard_blocks_are_padded_to_bytes()
        {
            // md_Bool : Array[0..2, 0..4] of Bool in a standard DB (BT_S_Arrays on the 1512SP): [1,0] has id 8, [2,4] id 20.
            var vte = ArrayM(Softdatatype.S7COMMP_SOFTDATATYPE_BOOL, [0, 0], [5, 3]);
            Assert.True(SymbolBrowser.TryGetArrayElementId(vte, [1, 0], out uint a, out _, out _));
            Assert.True(SymbolBrowser.TryGetArrayElementId(vte, [2, 4], out uint b, out _, out _));
            Assert.Equal(8u, a);
            Assert.Equal(20u, b);
        }

        [Fact]
        public void Fb_array_is_a_multi_dimensional_array()
        {
            // o1md : Array[0..1, 1..2] of "BT_FB_O1": the type info keeps even one dimension in the Mdim fields.
            var oi = new POffsetInfoType_FbArray { ArrayElementCount = 4 };
            oi.MdimArrayLowerBounds[0] = 1;
            oi.MdimArrayElementCount[0] = 2;
            oi.MdimArrayElementCount[1] = 2;
            var vte = new PVartypeListElement { Softdatatype = Softdatatype.S7COMMP_SOFTDATATYPE_STRUCT, OffsetInfoType = oi };
            Assert.True(oi.IsMDim());
            Assert.True(SymbolBrowser.TryGetArrayElementId(vte, [1, 2], out uint id, out int lb, out _));
            Assert.Equal(3u, id);
            Assert.Equal(1, lb);
        }
    }

    public class S7CommPlusValueTests
    {
        [Theory]
        [InlineData(0x0000, 0)]
        [InlineData(0x0123, 1230)]       // 123 x 10 ms
        [InlineData(0x1999, 99900)]      // 999 x 100 ms
        [InlineData(0x2010, 10000)]      // 10 x 1 s
        [InlineData(0x3100, 1000000)]    // 100 x 10 s
        public void S5time_is_decoded_to_ms(int raw, double expected)
        {
            Assert.True(ValueConverter.TryDecodeS5Time((ushort)raw, out double ms));
            Assert.Equal(expected, ms);
        }

        [Fact]
        public void Dates_are_converted_to_unix_ms()
        {
            double Expect(int y, int mo, int d, int h = 0, int mi = 0, int sec = 0, int ms = 0) =>
                (new DateTime(y, mo, d, h, mi, sec, ms, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds;

            // DATE: days since 1990-01-01
            Assert.True(ValueConverter.TryToDouble(new ValueUInt(13073), Softdatatype.S7COMMP_SOFTDATATYPE_DATE, out double date));
            Assert.Equal(Expect(2025, 10, 17), date);

            // DATE_AND_TIME: BCD 26-10-17 12:34:56.789, weekday 7
            var dt = new ValueUSIntArray(new byte[] { 0x26, 0x10, 0x17, 0x12, 0x34, 0x56, 0x78, 0x97 });
            Assert.True(ValueConverter.TryToDouble(dt, Softdatatype.S7COMMP_SOFTDATATYPE_DATEANDTIME, out double dtMs));
            Assert.Equal(Expect(2026, 10, 17, 12, 34, 56, 789), dtMs);

            // LDT: ns since 1970
            ulong ns = (ulong)Expect(2026, 1, 2, 3, 4, 5, 6) * 1_000_000UL;
            Assert.True(ValueConverter.TryToDouble(new ValueTimestamp(ns), Softdatatype.S7COMMP_SOFTDATATYPE_LDT, out double ldt));
            Assert.Equal(Expect(2026, 1, 2, 3, 4, 5, 6), ldt);

            // DTL bytes: 2026-10-17 (weekday 7) 12:34:56.000500000
            Assert.True(ValueConverter.TryDecodeDtl(new byte[] { 0x07, 0xEA, 10, 17, 7, 12, 34, 56, 0x1D, 0xCD, 0x65, 0x00 }, out double dtl));
            Assert.Equal(Expect(2026, 10, 17, 12, 34, 56) + 500, dtl);
        }

        [Fact]
        public void Invalid_dates_are_rejected()
        {
            Assert.False(ValueConverter.TryDecodeDateAndTime(new byte[] { 0x26, 0x13, 0x17, 0, 0, 0, 0, 0 }, out _));
            Assert.False(ValueConverter.TryDecodeDtl(new byte[] { 0x07, 0xEA, 2, 30, 1, 0, 0, 0, 0, 0, 0, 0 }, out _));
        }

        [Fact]
        public void Loggable_types()
        {
            Assert.True(SymbolBrowser.IsLoggable(Softdatatype.S7COMMP_SOFTDATATYPE_DTL));
            Assert.True(SymbolBrowser.IsLoggable(Softdatatype.S7COMMP_SOFTDATATYPE_REAL));
            Assert.False(SymbolBrowser.IsLoggable(Softdatatype.S7COMMP_SOFTDATATYPE_STRING));
            Assert.False(SymbolBrowser.IsLoggable(Softdatatype.S7COMMP_SOFTDATATYPE_WSTRING));
        }

        [Fact]
        public void Invalid_bcd_is_rejected()
        {
            Assert.False(ValueConverter.TryDecodeS5Time(0x00AF, out _));
        }

        [Fact]
        public void Values_convert_to_double()
        {
            Assert.True(ValueConverter.TryToDouble(new ValueBool(true), Softdatatype.S7COMMP_SOFTDATATYPE_BOOL, out double b));
            Assert.Equal(1, b);
            Assert.True(ValueConverter.TryToDouble(new ValueLInt(-5), Softdatatype.S7COMMP_SOFTDATATYPE_LINT, out double l));
            Assert.Equal(-5, l);
            Assert.True(ValueConverter.TryToDouble(new ValueTimespan(2_500_000), Softdatatype.S7COMMP_SOFTDATATYPE_LTIME, out double lt));
            Assert.Equal(2.5, lt);
            Assert.True(ValueConverter.TryToDouble(new ValueDInt(1500), Softdatatype.S7COMMP_SOFTDATATYPE_TIME, out double t));
            Assert.Equal(1500, t);
            Assert.False(ValueConverter.TryToDouble(new ValueReal(float.NaN), Softdatatype.S7COMMP_SOFTDATATYPE_REAL, out _));
            Assert.False(ValueConverter.TryToDouble(new ValueWString("x"), Softdatatype.S7COMMP_SOFTDATATYPE_WSTRING, out _));
        }
    }

    public class S7CommPlusNotificationTests
    {
        private static byte[] BuildNotification(bool withTimestamp, Action<MemoryStream> items)
        {
            var ms = new MemoryStream();
            ms.WriteByte(ProtocolVersion.V2);
            ms.WriteByte(Opcode.Notification);
            S7p.EncodeUInt32(ms, 0x12345678);
            S7p.EncodeUInt16(ms, 0);
            S7p.EncodeUInt16(ms, 0);
            S7p.EncodeUInt16(ms, 0);
            ms.WriteByte(0);                           // credit tick
            S7p.EncodeUInt32Vlq(ms, 7);                // sequence number
            if (withTimestamp)
            {
                // 0 change counter → u64 µs timestamp (first byte 0), then the change counter
                ulong micros = (ulong)(new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks / 10;
                S7p.EncodeUInt64(ms, micros);
                ms.WriteByte(1);
            }
            else
            {
                ms.WriteByte(1);
            }
            items(ms);
            ms.WriteByte(0);                           // end of items
            ms.WriteByte(0);                           // no alarm part
            return ms.ToArray();
        }

        [Fact]
        public void Values_and_item_errors_are_decoded()
        {
            var pdu = BuildNotification(false, ms =>
            {
                ms.WriteByte(0x9B);
                S7p.EncodeUInt32Vlq(ms, 1);
                new ValueReal(1.5f).Serialize(ms);
                ms.WriteByte(0x03);
                S7p.EncodeUInt32(ms, 2);
            });
            var n = Notification.DeserializeFromPdu(new MemoryStream(pdu));
            Assert.Equal(0x12345678u, n.SubscriptionObjectId);
            Assert.Single(n.Values);
            Assert.Equal(1.5f, ((ValueReal)n.Values[1]).GetValue());
            Assert.Equal(0x03, n.ReturnValues[2]);
            Assert.False(n.Truncated);
        }

        [Fact]
        public void Plc_timestamp_variant()
        {
            var pdu = BuildNotification(true, ms =>
            {
                ms.WriteByte(0x92);
                S7p.EncodeUInt32(ms, 1);
                new ValueBool(true).Serialize(ms);
            });
            var n = Notification.DeserializeFromPdu(new MemoryStream(pdu));
            Assert.Equal(new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc), n.Add1Timestamp);
            Assert.True(((ValueBool)n.Values[1]).GetValue());
        }

        [Fact]
        public void Unknown_item_code_stops_without_exception()
        {
            var pdu = BuildNotification(false, ms =>
            {
                ms.WriteByte(0x92);
                S7p.EncodeUInt32(ms, 1);
                new ValueInt(5).Serialize(ms);
                ms.WriteByte(0x83);
                ms.WriteByte(0xFF);
            });
            var n = Notification.DeserializeFromPdu(new MemoryStream(pdu));
            Assert.True(n.Truncated);
            Assert.Single(n.Values);
        }
    }

    public class S7CommPlusSubscriptionListTests
    {
        [Fact]
        public void Reference_list_layout()
        {
            var a = new ItemAddress(0x8A0E0001, Ids.DB_ValueActual) { SymbolCrc = 0xAABBCCDD };
            a.LID.AddRange([0xA, 0x2]);
            var list = S7CommPlusConnection.BuildSubscriptionReferenceList([a], 1).GetValue();
            Assert.Equal(new uint[]
            {
                0x80010000, 0, 1,
                0x80040003, 1, 0, 0x8A0E0001, 0xAABBCCDD, (uint)Ids.DB_ValueActual, 0xA, 0x2
            }, list);
        }
    }
}
