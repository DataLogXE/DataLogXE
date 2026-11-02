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

namespace DataLogApi.Services
{
    /// <summary>
    /// The Caddyfile of the proxy service (Proxy/Caddyfile, embedded), so it always fits this version of the app.
    /// </summary>
    public class ProxyConfigAppService
    {
        public string Caddyfile { get; }

        public ProxyConfigAppService()
        {
            using var stream = typeof(ProxyConfigAppService).Assembly.GetManifestResourceStream("Caddyfile")
                ?? throw new InvalidOperationException("The embedded Caddyfile is missing.");
            using var reader = new StreamReader(stream);
            Caddyfile = reader.ReadToEnd();
        }
    }
}
