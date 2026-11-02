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

using S7CommPlusDriver;
using S7CommPlusDriver.Alarming;
using Xunit;

namespace DataLogLib.Tests.S7CommPlus
{
    public class S7CommPlusAlarmTests
    {
        private const int German = 0x0407;
        private const int English = 0x0409;

        private static AlarmDai Single(string hex)
        {
            var n = Notification.DeserializeFromPdu(new MemoryStream(Convert.FromHexString(hex)));
            Assert.NotNull(n);
            Assert.False(n.Truncated);
            return Assert.Single(AlarmDai.FromNotification(n));
        }

        [Fact]
        public void Program_alarm_coming_and_going_are_decoded()
        {
            var coming = Single(S7CommPlusAlarmFixtures.ProgramAlarmComing);
            var going = Single(S7CommPlusAlarmFixtures.ProgramAlarmGoing);

            Assert.Null(coming.DecodeError);
            Assert.Equal(0x8A0E000B00010000UL, coming.CpuAlarmId);
            Assert.Equal(coming.CpuAlarmId, going.CpuAlarmId);
            Assert.Equal((ushort)256, coming.Domain);
            Assert.Equal(AlarmMessageType.Notify, coming.MessageType);
            Assert.True(coming.IsActive);
            Assert.False(going.IsActive);
            Assert.Equal(0x87, coming.AllStatesInfo);
            Assert.Equal(0x86, going.AllStatesInfo);
            // Coming and its going share the sequence counter.
            Assert.Equal(coming.SequenceCounter, going.SequenceCounter);
            Assert.NotNull(coming.Coming?.TimestampUtc);
            Assert.Same(going.Going, going.Current);
            Assert.True(going.LatestTimeUtc > coming.LatestTimeUtc);
            Assert.Equal((ushort)34, coming.HmiInfo.AlarmClass);
            Assert.Equal(0x200u, coming.HmiInfo.ClientAlarmId);
            Assert.Equal(" ", coming.Texts.Get(German, AlarmTexts.AlarmText));
        }

        [Fact]
        public void System_info_report_has_texts_in_both_languages_and_its_placeholder_resolved()
        {
            var dai = Single(S7CommPlusAlarmFixtures.OpcUaSessionCreated);

            Assert.Null(dai.DecodeError);
            Assert.Equal(AlarmDai.DomainSystemDiagnostics, dai.Domain);
            Assert.Equal(AlarmMessageType.InfoReport, dai.MessageType);
            Assert.Contains(German, dai.Texts.Languages);
            Assert.Contains(English, dai.Texts.Languages);
            Assert.Equal("MyPlc1512SP", dai.Texts.Get(German, AlarmTexts.AdditionalText1));
            var text = dai.Texts.Get(German, AlarmTexts.AlarmText);
            Assert.Contains("Session-ID: @5X%u@", text);
            Assert.Equal(20, dai.Coming.DiagnosticData.Length);

            Assert.Contains("Session-ID: 18800.", AlarmTextFormatter.Format(text, dai.Current, resolveDiagnostic: true));
            Assert.Contains("Session-ID: @5X%u@.", AlarmTextFormatter.Format(text, dai.Current));
        }

        [Fact]
        public void Collective_message_gets_its_seconds()
        {
            var dai = Single(S7CommPlusAlarmFixtures.CollectiveSecurityChecks);
            var formatted = AlarmTextFormatter.Format(dai.Texts.Get(German, AlarmTexts.AlarmText), dai.Current, resolveDiagnostic: true);
            Assert.Contains("während der letzten 20 Sekunde(n)", formatted);
        }

        [Fact]
        public void Security_event_is_in_its_own_domain()
        {
            var dai = Single(S7CommPlusAlarmFixtures.SecurityLoginDenied);
            Assert.Equal((ushort)3, dai.Domain);
            Assert.StartsWith("Security-Ereignis: Anmeldung für OPC UA-Server Anwender verweigert", dai.Texts.Get(German, AlarmTexts.AlarmText));
            Assert.StartsWith("Security event", dai.Texts.Get(English, AlarmTexts.AlarmText));
        }

        [Fact]
        public void Missing_attributes_do_not_throw()
        {
            var obj = new PObject { ClassId = Ids.DAI_Class_Rid };
            obj.AddAttribute(Ids.DAI_CPUAlarmID, new ValueLWord(42));
            var dai = AlarmDai.FromObject(obj);
            Assert.Equal(42UL, dai.CpuAlarmId);
            Assert.Null(dai.Coming);
            Assert.Null(dai.Going);
            Assert.Null(dai.HmiInfo);
            Assert.Contains("Coming/Going", dai.DecodeError);
        }

        [Fact]
        public void Language_falls_back_to_english_then_to_the_first_one()
        {
            var texts = new Dictionary<uint, ValueBlobSparseArray.BlobEntry>
            {
                [0x04090002] = new ValueBlobSparseArray.BlobEntry { value = "en"u8.ToArray() },
                [0x040C0002] = new ValueBlobSparseArray.BlobEntry { value = "fr"u8.ToArray() },
            };
            var at = AlarmTexts.FromSparseArray(new ValueBlobSparseArray(texts));
            Assert.Equal(English, at.ChooseLanguage(German));
            Assert.Equal(0x040C, at.ChooseLanguage(0x040C));

            texts.Remove(0x04090002);
            Assert.Equal(0x040C, AlarmTexts.FromSparseArray(new ValueBlobSparseArray(texts)).ChooseLanguage(German));
        }

        [Fact]
        public void Program_alarm_values_are_formatted()
        {
            var t = new AlarmTransition();
            t.Values[1] = AlarmAssociatedValue.Decode(Ids.TI_INT, new byte[] { 0xFF, 0x85 });
            t.Values[2] = AlarmAssociatedValue.Decode(Ids.TI_REAL, BitConverter.GetBytes(12.345f).Reverse().ToArray());
            t.Values[3] = AlarmAssociatedValue.Decode(Ids.TI_STRING_START + 10, new byte[] { 10, 3, (byte)'a', (byte)'b', (byte)'c' });
            t.Values[4] = AlarmAssociatedValue.Decode(Ids.TI_BOOL, new byte[] { 1 });

            Assert.Equal("v=-123 r=12.35 s=abc b=1 missing=@7%d@",
                AlarmTextFormatter.Format("v=@1%d@ r=@2%.2f@ s=@3%s@ b=@4%d@ missing=@7%d@", t));
        }

        [Fact]
        public void Diagnostic_placeholder_outside_the_data_stays()
        {
            var t = new AlarmTransition { DiagnosticData = new byte[] { 1, 2, 3, 4 } };
            Assert.Equal("a=0x1020304 b=@4X%u@ c=@2Q%u@",
                AlarmTextFormatter.Format("a=0x@1X%X@ b=@4X%u@ c=@2Q%u@", t, resolveDiagnostic: true));
        }

        [Fact]
        public void Alarm_subscription_object_takes_all_domains_without_credit_limit()
        {
            var obj = S7CommPlusConnection.BuildAlarmSubscriptionObject(0x7fffc001, 0x51010123, new uint[] { 0x0407, 0x0409 });
            Assert.Equal((uint)Ids.ClassSubscription, obj.ClassId);
            Assert.Equal(2, obj.TryGetAttribute<ValueUSInt>(Ids.SubscriptionFunctionClassId).GetValue());
            Assert.Equal(-1, obj.TryGetAttribute<ValueInt>(Ids.SubscriptionCreditLimit).GetValue());
            var asref = Assert.Single(obj.GetObjectsByClassId(Ids.AlarmSubscriptionRef_Class_Rid));
            Assert.Equal(0x51010123u, asref.RelationId);
            Assert.Equal(new ushort[] { 65535 }, asref.TryGetAttribute<ValueUIntArray>(Ids.AlarmSubscriptionRef_AlarmDomain2).GetValue());
            Assert.Equal(new uint[] { 0x0407, 0x0409 }, asref.TryGetAttribute<ValueUDIntArray>(Ids.AlarmSubscriptionRef_AlarmTextLanguages_Rid).GetValue());
            Assert.True(asref.TryGetAttribute<ValueBool>(Ids.AlarmSubscriptionRef_SendAlarmTexts_Rid).GetValue());
            Assert.Equal((uint)Ids.NativeObjects_theAlarmSubsystem_Rid, asref.Relations[Ids.AlarmSubscriptionRef_itsAlarmSubsystem]);

            var ms = new MemoryStream();
            Assert.True(obj.Serialize(ms) > 0);
        }

        [Fact]
        public void Notification_without_alarm_part_has_no_alarms()
        {
            Assert.Empty(AlarmDai.FromNotification(new Notification(ProtocolVersion.V2)));
        }
    }
}
