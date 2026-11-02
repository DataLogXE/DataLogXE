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

using DataLogLib.Common;
using Serilog;

namespace DataLogService.DataSource.System.Telegraf
{
    internal class TelegrafParser : IDisposable
    {
        #region private fields
        private readonly int _port;
        private readonly SimpleSocketServer _socketServer;
        private readonly TelegrafDiskParser _diskParser;
        private readonly TelegrafSwapParser _swapParser;
        private readonly TelegrafSystemParser _systemParser;

        private readonly Action<TelegrafValue> _onValue;
        #endregion

        #region construction
        public TelegrafParser(int port, Action<TelegrafValue> onValue)
        {
            _port = port;
            _onValue = onValue;

            // The parsers must exist before the server accepts the first line.
            _diskParser = new TelegrafDiskParser(onValue);
            _swapParser = new TelegrafSwapParser(onValue);
            _systemParser = new TelegrafSystemParser(onValue);

            Log.Information("Start telegraf parser on port {TelegrafPort}", _port);
            _socketServer = new SimpleSocketServer(_port);
            _socketServer.OnMessageReceived += OnMessageReceived;
            _socketServer.Start();
        }
        #endregion

        #region public methods
        public void Dispose()
        {
            _socketServer.Stop();
        }
        #endregion

        #region private methods
        private void OnMessageReceived(string message)
        {
            int commaIndex = message.IndexOf(',');
            if (commaIndex > 0)
            {
                var typeString = message.Substring(0, commaIndex);

                switch (typeString)
                {
                    case "disk":
                        _diskParser.OnMessage(message);
                        break;

                    case "swap":
                        _swapParser.OnMessage(message);
                        break;

                    case "system":
                        _systemParser.OnMessage(message);
                        break;
                }
            }
        }
        #endregion

    }
}
