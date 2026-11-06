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

using Serilog.Core;
using Serilog.Events;
using System.Text.RegularExpressions;

namespace DataLogLib.Common.Serilog
{
    /// <summary>
    /// Keeps secrets out of the log. Properties named like a secret are replaced by "***" before any sink sees the
    /// event, so neither the console, the log file nor the database get them; text that carries one inline (a
    /// connection string, a bearer header) is masked by <see cref="Redact"/>, also for exceptions and the log files
    /// in the support bundle.
    /// </summary>
    public static partial class LogRedaction
    {
        public const string Mask = "***";

        private static readonly string[] SecretNameEndings =
            ["password", "passwd", "pwd", "secret", "token", "apikey", "masterkey", "privatekey", "credential", "credentials"];

        // Cheap pre-check before the regex runs on a text
        private static readonly string[] SecretHints = ["pass", "pwd", "secret", "token", "key", "bearer", "credential"];

        /// <summary>A property whose name ends in a secret word, for example Password, DbPassword or ApiKey.</summary>
        public static bool IsSecretName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (var ending in SecretNameEndings)
            {
                if (name.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Masks "Password=...", "token: ..." and "Bearer ..." in a text.</summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text) || !MayHoldSecret(text))
            {
                return text;
            }

            text = KeyValueSecret().Replace(text, match => match.Groups["key"].Value + match.Groups["sep"].Value + Mask);
            return BearerSecret().Replace(text, "Bearer " + Mask);
        }

        private static bool MayHoldSecret(string text)
        {
            foreach (var hint in SecretHints)
            {
                if (text.Contains(hint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        [GeneratedRegex(
            @"(?<key>\b[\w.-]*(?:password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)\b)(?<sep>\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^;\s,&""']+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex KeyValueSecret();

        [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex BearerSecret();
    }

    /// <summary>Applies <see cref="LogRedaction"/> to the properties of every log event.</summary>
    public sealed class SecretRedactionEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            List<LogEventProperty> replaced = null;
            foreach (var pair in logEvent.Properties)
            {
                LogEventPropertyValue value = null;
                if (LogRedaction.IsSecretName(pair.Key))
                {
                    value = new ScalarValue(LogRedaction.Mask);
                }
                else if (pair.Value is ScalarValue { Value: string text })
                {
                    var redacted = LogRedaction.Redact(text);
                    if (!ReferenceEquals(redacted, text) && redacted != text)
                    {
                        value = new ScalarValue(redacted);
                    }
                }

                if (value != null)
                {
                    (replaced ??= []).Add(new LogEventProperty(pair.Key, value));
                }
            }

            // Not while iterating: the properties are a dictionary
            if (replaced != null)
            {
                foreach (var property in replaced)
                {
                    logEvent.AddOrUpdateProperty(property);
                }
            }
        }
    }
}
