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

using DataLogLib.Common.Serilog;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace DataLogLib.Tests
{
    public class LogRedactionTests
    {
        [Theory]
        [InlineData("Host=db;Username=x;Password=s3cr3t;Pool=1", "Host=db;Username=x;Password=***;Pool=1")]
        [InlineData("password: \"a b c\" next", "password: *** next")]
        [InlineData("db_password=abc&x=1", "db_password=***&x=1")]
        [InlineData("ApiKey = 123456", "ApiKey = ***")]
        [InlineData("Authorization: Bearer eyJhbGciOi.abc-def_1==", "Authorization: Bearer ***")]
        [InlineData("token=abc, other", "token=***, other")]
        public void MasksInlineSecrets(string text, string expected)
        {
            Assert.Equal(expected, LogRedaction.Redact(text));
        }

        [Theory]
        [InlineData("Connected to 10.0.0.5:4840")]
        [InlineData("NodeKey: ns=2;s=Pump.Speed")]
        [InlineData("Auth: admin changed user bob (role viewer, password reset)")]
        [InlineData("")]
        public void LeavesOtherTextAlone(string text)
        {
            Assert.Equal(text, LogRedaction.Redact(text));
        }

        [Theory]
        [InlineData("Password", true)]
        [InlineData("DbPassword", true)]
        [InlineData("ApiKey", true)]
        [InlineData("RefreshToken", true)]
        [InlineData("PasswordAction", false)]
        [InlineData("NodeKey", false)]
        [InlineData("DsName", false)]
        public void KnowsSecretNames(string name, bool secret)
        {
            Assert.Equal(secret, LogRedaction.IsSecretName(name));
        }

        [Fact]
        public void EnricherMasksPropertiesBeforeTheMessageIsRendered()
        {
            var template = new MessageTemplateParser().Parse("Login {UserName} with {Password} via {Url}");
            var logEvent = new LogEvent(
                DateTimeOffset.UtcNow,
                LogEventLevel.Information,
                null,
                template,
                [
                    new LogEventProperty("UserName", new ScalarValue("bob")),
                    new LogEventProperty("Password", new ScalarValue("hunter2")),
                    new LogEventProperty("Url", new ScalarValue("https://x/?token=abc"))
                ]);

            new SecretRedactionEnricher().Enrich(logEvent, null);

            Assert.Equal("Login \"bob\" with \"***\" via \"https://x/?token=***\"", logEvent.RenderMessage());
        }
    }
}
