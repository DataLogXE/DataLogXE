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

using System.Net;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace DataLogLib.Common
{
    /// <summary>
    /// Line based TCP receiver (Telegraf socket_writer). Every client has its own buffer and decoder,
    /// all threads are background threads, and Stop closes the listener and every open connection.
    /// </summary>
    public class SimpleSocketServer
    {
        // A line longer than this without a newline is not line protocol; drop it instead of growing forever.
        private const int MaxPendingChars = 1024 * 1024;

        private readonly TcpListener _server;
        private readonly object _clientsLock = new();
        private readonly HashSet<TcpClient> _clients = [];
        private volatile bool _isRunning;

        // Event triggered when a message is received
        public event Action<string> OnMessageReceived;

        // Initializes the server with the IP address and port
        public SimpleSocketServer(int port)
        {
            _server = new TcpListener(IPAddress.Any, port);
        }

        // Starts the server
        public void Start()
        {
            _server.Start();
            _isRunning = true;
            Log.Information("Server started and waiting for connections...");

            new Thread(Run) { IsBackground = true, Name = "SocketServerAccept" }.Start();
        }

        // Stops the server and closes all client connections
        public void Stop()
        {
            _isRunning = false;
            try
            {
                _server.Stop();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Socket server listener stop failed");
            }

            List<TcpClient> clients;
            lock (_clientsLock)
            {
                clients = [.. _clients];
                _clients.Clear();
            }

            foreach (var client in clients)
            {
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // Already closed by the peer.
                }
            }

            Log.Information("Server stopped.");
        }

        // Main server loop that waits for connections
        private void Run()
        {
            while (_isRunning)
            {
                TcpClient client;
                try
                {
                    client = _server.AcceptTcpClient();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    if (!_isRunning)
                    {
                        break;
                    }

                    Log.Error(ex, "Socket server accept failed");
                    Thread.Sleep(1000);
                    continue;
                }

                lock (_clientsLock)
                {
                    if (!_isRunning)
                    {
                        client.Close();
                        break;
                    }

                    _clients.Add(client);
                }

                Log.Information("A telegraf client has connected.");
                new Thread(() => HandleClient(client)) { IsBackground = true, Name = "SocketServerClient" }.Start();
            }
        }

        // Reads one client until it disconnects or the server stops
        private void HandleClient(TcpClient client)
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[16 * 1024];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var pending = new StringBuilder();

            try
            {
                using var stream = client.GetStream();
                while (_isRunning)
                {
                    // Blocking read: 0 means the peer closed the connection.
                    var bytesRead = stream.Read(bytes, 0, bytes.Length);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    // The decoder keeps a multi-byte character that is split across two reads.
                    var charCount = decoder.GetChars(bytes, 0, bytesRead, chars, 0);
                    pending.Append(chars, 0, charCount);
                    ProcessBuffer(pending);

                    if (pending.Length > MaxPendingChars)
                    {
                        Log.Warning("Socket server dropped {Count} chars without line break", pending.Length);
                        pending.Clear();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                if (_isRunning)
                {
                    Log.Warning("Socket client connection lost: {ClientMessage}", ex.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error while handling client");
            }
            finally
            {
                lock (_clientsLock)
                {
                    _clients.Remove(client);
                }

                client.Close();
                Log.Information("Client connection closed.");
            }
        }

        // Raises the event for every complete line and keeps the incomplete rest
        private void ProcessBuffer(StringBuilder pending)
        {
            var text = pending.ToString();
            var start = 0;
            int lineBreakIndex;
            while ((lineBreakIndex = text.IndexOf('\n', start)) != -1)
            {
                var line = text[start..lineBreakIndex].Trim();
                start = lineBreakIndex + 1;
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    OnMessageReceived?.Invoke(line);
                }
                catch (Exception ex)
                {
                    // One bad line must not close the connection.
                    Log.Error(ex, "Socket server message handler failed");
                }
            }

            if (start > 0)
            {
                pending.Remove(0, start);
            }
        }
    }
}
