#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 *
 * Alarm object layout (DAI, AS_CGS, HmiInfo, texts, associated values) ported from
 * S7CommPlusDriver (Alarming/AlarmsDai.cs, AlarmsAsCgs.cs, AlarmsHmiInfo.cs, AlarmsAlarmTexts.cs,
 * AlarmsAssociatedValues.cs) by Thomas Wiens.
 /****************************************************************************/
#endregion

namespace S7CommPlusDriver.Alarming
{
    /// <summary>Message type of an alarm (DAI.MessageType).</summary>
    public enum AlarmMessageType
    {
        Unknown = 0,
        Alarm = 1,
        Notify = 2,
        InfoReport = 3,
        EventAck = 4,
    }

    /// <summary>
    /// One alarm as the PLC sends it in an alarm notification or in the active alarm list (class DAI).
    /// Decoding never throws: missing or unexpected attributes leave the field empty and are named in <see cref="DecodeError"/>.
    /// </summary>
    public sealed class AlarmDai
    {
        /// <summary>Alarm domain of the system diagnostics; security events use 2 and 3, the program alarm classes 256..272.</summary>
        public const ushort DomainSystemDiagnostics = 1;
        public const ushort DomainUserClassFirst = 256;

        public string ObjectName = "";
        public ulong CpuAlarmId;
        public byte AllStatesInfo;
        public ushort Domain;
        public int MessageTypeRaw;
        public uint SequenceCounter;
        public AlarmHmiInfo HmiInfo;
        public AlarmTransition Coming;
        public AlarmTransition Going;
        public AlarmTexts Texts = new AlarmTexts();
        public string DecodeError;

        public AlarmMessageType MessageType =>
            MessageTypeRaw >= 1 && MessageTypeRaw <= 4 ? (AlarmMessageType)MessageTypeRaw : AlarmMessageType.Unknown;

        /// <summary>
        /// Bit 0 of the state info is set while the alarm is active
        /// (seen on a 1512SP FW 4.1: 0x87 coming, 0x86 going).
        /// </summary>
        public bool IsActive => (AllStatesInfo & 0x01) != 0;

        /// <summary>
        /// Bit 1 is set when the alarm is acknowledged or needs no acknowledgement
        /// (seen on a 1512SP: program alarm with acknowledgement 0x85 coming / 0x84 going while unacknowledged,
        /// without acknowledgement 0x87 / 0x86).
        /// </summary>
        public bool IsAcknowledged => (AllStatesInfo & 0x02) != 0;

        /// <summary>The transition with the latest PLC time (coming, going or the acknowledgement of one of them).</summary>
        public DateTime? LatestTimeUtc
        {
            get
            {
                DateTime? latest = null;
                foreach (var t in new[] { Coming?.TimestampUtc, Coming?.AckTimestampUtc, Going?.TimestampUtc, Going?.AckTimestampUtc })
                {
                    if (t.HasValue && (!latest.HasValue || t.Value > latest.Value))
                    {
                        latest = t;
                    }
                }
                return latest;
            }
        }

        /// <summary>The transition the latest state change belongs to: going when the alarm is inactive and has a going time.</summary>
        public AlarmTransition Current =>
            !IsActive && Going?.TimestampUtc != null ? Going : (Coming ?? Going);

        /// <summary>All alarm objects of a notification; empty when the notification carries none.</summary>
        public static List<AlarmDai> FromNotification(Notification notification)
        {
            var list = new List<AlarmDai>();
            if (notification?.P2Objects == null || notification.P2ReturnValue != 0x81)
            {
                return list;
            }
            foreach (var obj in notification.P2Objects)
            {
                if (obj != null && obj.ClassId == Ids.DAI_Class_Rid)
                {
                    list.Add(FromObject(obj));
                }
            }
            return list;
        }

        public static AlarmDai FromObject(PObject obj)
        {
            var dai = new AlarmDai();
            var missing = new List<string>();
            try
            {
                dai.ObjectName = obj.TryGetAttribute<ValueWString>(Ids.ObjectVariableTypeName)?.GetValue() ?? "";
                var id = obj.TryGetAttribute<ValueLWord>(Ids.DAI_CPUAlarmID);
                if (id != null) dai.CpuAlarmId = id.GetValue(); else missing.Add("CPUAlarmID");
                var states = obj.TryGetAttribute<ValueUSInt>(Ids.DAI_AllStatesInfo);
                if (states != null) dai.AllStatesInfo = states.GetValue(); else missing.Add("AllStatesInfo");
                var domain = obj.TryGetAttribute<ValueUInt>(Ids.DAI_AlarmDomain);
                if (domain != null) dai.Domain = domain.GetValue(); else missing.Add("AlarmDomain");
                var type = obj.TryGetAttribute<ValueDInt>(Ids.DAI_MessageType);
                if (type != null) dai.MessageTypeRaw = type.GetValue(); else missing.Add("MessageType");
                var seq = obj.TryGetAttribute<ValueUDInt>(Ids.DAI_SequenceCounter);
                if (seq != null) dai.SequenceCounter = seq.GetValue();
                var hmi = obj.TryGetAttribute<ValueBlob>(Ids.DAI_HmiInfo);
                dai.HmiInfo = hmi != null ? AlarmHmiInfo.FromBytes(hmi.GetValue()) : null;
                dai.Coming = AlarmTransition.FromStruct(obj.TryGetAttribute<ValueStruct>(Ids.DAI_Coming));
                dai.Going = AlarmTransition.FromStruct(obj.TryGetAttribute<ValueStruct>(Ids.DAI_Going));
                if (dai.Coming == null && dai.Going == null) missing.Add("Coming/Going");
                dai.Texts = AlarmTexts.FromSparseArray(obj.TryGetAttribute<ValueBlobSparseArray>(Ids.DAI_AlarmTexts_Rid));
            }
            catch (Exception ex)
            {
                missing.Add(ex.GetType().Name + ": " + ex.Message);
            }
            if (missing.Count > 0)
            {
                dai.DecodeError = "missing " + string.Join(", ", missing);
            }
            return dai;
        }
    }

    /// <summary>Coming or going part of an alarm (struct AS_CGS).</summary>
    public sealed class AlarmTransition
    {
        public byte StatesInfo;
        public DateTime? TimestampUtc;
        public DateTime? AckTimestampUtc;

        /// <summary>Typed associated values of a program alarm (SD_1..SD_10), by index.</summary>
        public SortedDictionary<int, AlarmAssociatedValue> Values = new SortedDictionary<int, AlarmAssociatedValue>();

        /// <summary>Untyped value block of a system diagnostic alarm (the texts address it by byte offset), or null.</summary>
        public byte[] DiagnosticData;

        public static AlarmTransition FromStruct(ValueStruct str)
        {
            if (str == null)
            {
                return null;
            }
            var t = new AlarmTransition();
            t.StatesInfo = str.TryGetStructElement<ValueUSInt>(Ids.AS_CGS_AllStatesInfo)?.GetValue() ?? 0;
            t.TimestampUtc = ToUtc(str.TryGetStructElement<ValueTimestamp>(Ids.AS_CGS_Timestamp));
            t.AckTimestampUtc = ToUtc(str.TryGetStructElement<ValueTimestamp>(Ids.AS_CGS_AckTimestamp));
            var blobs = str.TryGetStructElement<ValueBlobArray>(Ids.AS_CGS_AssociatedValues)?.GetValue();
            if (blobs != null)
            {
                // [0] is type information, [1..10] are SD_1..SD_10.
                for (int i = 1; i < blobs.Length && i <= 10; i++)
                {
                    var bytes = blobs[i]?.GetValue();
                    if (bytes == null || bytes.Length == 0)
                    {
                        continue;
                    }
                    if (blobs[i].BlobRootId == 0)
                    {
                        t.DiagnosticData ??= bytes;
                        continue;
                    }
                    var value = AlarmAssociatedValue.Decode(blobs[i].BlobRootId, bytes);
                    if (value != null)
                    {
                        t.Values[i] = value;
                    }
                }
            }
            return t;
        }

        private static DateTime? ToUtc(ValueTimestamp ts)
        {
            if (ts == null || ts.GetValue() == 0)
            {
                return null;
            }
            return Utils.DtFromValueTimestamp(ts.GetValue());
        }
    }

    /// <summary>HMI information of an alarm (DAI.HmiInfo blob).</summary>
    public sealed class AlarmHmiInfo
    {
        public ushort SyntaxId;
        public ushort Version;
        public uint ClientAlarmId;
        public byte Priority;
        public ushort AlarmClass;
        public byte Producer;
        public byte GroupId;
        public byte Flags;

        public static AlarmHmiInfo FromBytes(byte[] b)
        {
            if (b == null || b.Length < 9)
            {
                return null;
            }
            var h = new AlarmHmiInfo
            {
                SyntaxId = Utils.GetUInt16(b, 0),
                Version = Utils.GetUInt16(b, 2),
                ClientAlarmId = Utils.GetUInt32(b, 4),
                Priority = b[8],
            };
            // SyntaxId >= 257: 3 reserved bytes; >= 258: class, producer, group, flags.
            if (h.SyntaxId >= 258 && b.Length >= 17)
            {
                h.AlarmClass = Utils.GetUInt16(b, 12);
                h.Producer = b[14];
                h.GroupId = b[15];
                h.Flags = b[16];
            }
            return h;
        }
    }

    /// <summary>
    /// Alarm texts by language. The PLC sends keys 0xLLLLnnnn: LLLL = language id (0x0407 = de-DE),
    /// nnnn = text id (1 info text, 2 alarm text, 3..11 additional texts 1..9).
    /// </summary>
    public sealed class AlarmTexts
    {
        public const ushort InfoText = 1;
        public const ushort AlarmText = 2;
        public const ushort AdditionalText1 = 3;
        public const int LcidEnglish = 0x0409;

        private readonly Dictionary<int, Dictionary<ushort, string>> _byLanguage = new Dictionary<int, Dictionary<ushort, string>>();

        public IEnumerable<int> Languages => _byLanguage.Keys;

        public static AlarmTexts FromSparseArray(ValueBlobSparseArray texts)
        {
            var at = new AlarmTexts();
            var dict = texts?.GetValue();
            if (dict == null)
            {
                return at;
            }
            foreach (var entry in dict)
            {
                var bytes = entry.Value.value;
                if (bytes == null)
                {
                    continue;
                }
                int lcid = (int)(entry.Key >> 16);
                ushort textId = (ushort)(entry.Key & 0xffff);
                if (!at._byLanguage.TryGetValue(lcid, out var lang))
                {
                    lang = new Dictionary<ushort, string>();
                    at._byLanguage[lcid] = lang;
                }
                lang[textId] = System.Text.Encoding.UTF8.GetString(bytes);
            }
            return at;
        }

        /// <summary>The language to show: the wanted one, else English, else the first one sent.</summary>
        public int ChooseLanguage(int wantedLcid)
        {
            if (_byLanguage.ContainsKey(wantedLcid)) return wantedLcid;
            if (_byLanguage.ContainsKey(LcidEnglish)) return LcidEnglish;
            return _byLanguage.Count > 0 ? _byLanguage.Keys.Min() : wantedLcid;
        }

        public string Get(int lcid, ushort textId)
        {
            return _byLanguage.TryGetValue(lcid, out var lang) && lang.TryGetValue(textId, out var s) ? s : "";
        }
    }

    /// <summary>One typed associated value (SD_n) of a program alarm.</summary>
    public sealed class AlarmAssociatedValue
    {
        public uint TypeInfo;
        public bool? Bool;
        public long? Integer;
        public double? Real;
        public string Text;
        public byte[] Raw;

        public override string ToString()
        {
            if (Text != null) return Text;
            if (Bool.HasValue) return Bool.Value ? "1" : "0";
            if (Integer.HasValue) return Integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Real.HasValue) return Real.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Raw != null ? BitConverter.ToString(Raw) : "";
        }

        public static AlarmAssociatedValue Decode(uint typeInfo, byte[] b)
        {
            var v = new AlarmAssociatedValue { TypeInfo = typeInfo, Raw = b };
            try
            {
                switch (typeInfo)
                {
                    case Ids.TI_BOOL: v.Bool = b[0] != 0; break;
                    case Ids.TI_BYTE:
                    case Ids.TI_USINT: v.Integer = b[0]; break;
                    case Ids.TI_SINT: v.Integer = (sbyte)b[0]; break;
                    case Ids.TI_CHAR: v.Text = System.Text.Encoding.Latin1.GetString(b, 0, 1); break;
                    case Ids.TI_WORD:
                    case Ids.TI_UINT: v.Integer = Utils.GetUInt16(b, 0); break;
                    case Ids.TI_INT: v.Integer = Utils.GetInt16(b, 0); break;
                    case Ids.TI_DWORD:
                    case Ids.TI_UDINT: v.Integer = Utils.GetUInt32(b, 0); break;
                    case Ids.TI_DINT: v.Integer = Utils.GetInt32(b, 0); break;
                    case Ids.TI_REAL: v.Real = Utils.GetFloat(b, 0); break;
                    case Ids.TI_LREAL: v.Real = Utils.GetDouble(b, 0); break;
                    case Ids.TI_WCHAR: v.Text = ((char)Utils.GetUInt16(b, 0)).ToString(); break;
                    default:
                        if (typeInfo > Ids.TI_STRING_START && typeInfo <= Ids.TI_STRING_END)
                        {
                            int len = Math.Min(b[1], b.Length - 2);
                            v.Text = System.Text.Encoding.Latin1.GetString(b, 2, Math.Max(0, len));
                        }
                        else if (typeInfo > Ids.TI_WSTRING_START && typeInfo <= Ids.TI_WSTRING_END)
                        {
                            int len = Math.Min(Utils.GetUInt16(b, 2) * 2, b.Length - 4);
                            v.Text = System.Text.Encoding.BigEndianUnicode.GetString(b, 4, Math.Max(0, len));
                        }
                        break;
                }
            }
            catch (IndexOutOfRangeException)
            {
                // Too short for its type: keep the raw bytes only.
            }
            catch (ArgumentException)
            {
            }
            return v;
        }
    }
}
