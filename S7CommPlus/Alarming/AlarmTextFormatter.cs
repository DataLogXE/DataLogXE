#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 /****************************************************************************/
#endregion

using System.Globalization;
using System.Text.RegularExpressions;

namespace S7CommPlusDriver.Alarming
{
    /// <summary>
    /// Puts the associated values into an alarm text. Placeholders:
    /// <list type="bullet">
    /// <item><c>@n%f@</c> (program alarms): associated value SD_n, format d/i/u (integer), f/e/g (real), s (text), x/X (hex), c (char).</item>
    /// <item><c>@nT%f@</c> (system diagnostics, only with <c>resolveDiagnostic</c>): a big endian value at the 1-based
    /// byte offset n of the diagnostic data, T = Y (byte), W (word) or X (double word), format u/d/x/X. This reading fits
    /// some messages of a 1512SP ("Session-ID: @5X%u@", "letzten @18Y%u@ Sekunde(n)") but not others ("@10W%u@
    /// Vorkommen" gave 32768), so it is off by default.</item>
    /// </list>
    /// A placeholder that cannot be resolved stays in the text unchanged.
    /// </summary>
    public static class AlarmTextFormatter
    {
        private static readonly Regex Placeholder = new Regex(@"@(\d{1,3})([A-Z]?)%(-?\d*(?:\.\d+)?)([a-zA-Z])@", RegexOptions.CultureInvariant);

        public static string Format(string text, AlarmTransition transition, bool resolveDiagnostic = false)
        {
            if (string.IsNullOrEmpty(text) || transition == null || text.IndexOf('@') < 0)
            {
                return text ?? "";
            }
            return Placeholder.Replace(text, m =>
            {
                int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string size = m.Groups[2].Value;
                string spec = m.Groups[3].Value;
                char fmt = m.Groups[4].Value[0];
                string result = size.Length > 0
                    ? (resolveDiagnostic ? FormatDiagnostic(transition.DiagnosticData, n, size[0], fmt) : null)
                    : FormatValue(transition.Values.TryGetValue(n, out var v) ? v : null, spec, fmt);
                return result ?? m.Value;
            });
        }

        private static string FormatDiagnostic(byte[] data, int offset1, char size, char fmt)
        {
            int width = size switch { 'Y' => 1, 'W' => 2, 'X' => 4, _ => 0 };
            int start = offset1 - 1;
            if (data == null || width == 0 || start < 0 || start + width > data.Length)
            {
                return null;
            }
            ulong value = 0;
            for (int i = 0; i < width; i++)
            {
                value = (value << 8) | data[start + i];
            }
            switch (fmt)
            {
                case 'u':
                    return value.ToString(CultureInfo.InvariantCulture);
                case 'd':
                case 'i':
                    long signed = width switch { 1 => (sbyte)value, 2 => (short)value, _ => (int)value };
                    return signed.ToString(CultureInfo.InvariantCulture);
                case 'x':
                    return value.ToString("x", CultureInfo.InvariantCulture);
                case 'X':
                    return value.ToString("X", CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        private static string FormatValue(AlarmAssociatedValue v, string spec, char fmt)
        {
            if (v == null)
            {
                return null;
            }
            int precision = -1;
            int dot = spec.IndexOf('.');
            if (dot >= 0 && int.TryParse(spec.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int p))
            {
                precision = p;
            }
            switch (fmt)
            {
                case 'd':
                case 'i':
                case 'u':
                    if (v.Integer.HasValue) return v.Integer.Value.ToString(CultureInfo.InvariantCulture);
                    if (v.Bool.HasValue) return v.Bool.Value ? "1" : "0";
                    if (v.Real.HasValue) return Math.Round(v.Real.Value).ToString(CultureInfo.InvariantCulture);
                    return v.Text;
                case 'f':
                case 'e':
                case 'g':
                    double? d = v.Real ?? v.Integer;
                    if (!d.HasValue) return v.Text;
                    return precision >= 0
                        ? d.Value.ToString("F" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                        : d.Value.ToString(CultureInfo.InvariantCulture);
                case 'x':
                case 'X':
                    if (v.Integer.HasValue) return v.Integer.Value.ToString(fmt == 'x' ? "x" : "X", CultureInfo.InvariantCulture);
                    return v.ToString();
                default:
                    return v.ToString();
            }
        }
    }
}
