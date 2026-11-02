#region License
/******************************************************************************
 * S7CommPlus driver for DataLogXE
 * Copyright (C) 2026 DataLogXE, licensed under LGPL-3.0-or-later.
 /****************************************************************************/
#endregion

using System.Threading.Channels;
using Serilog;

namespace S7CommPlusDriver.Subscriptions
{
    /// <summary>
    /// Decodes notification PDUs off the receive thread and routes them to the handler registered
    /// for the subscription object id. Notifications that arrive before the CreateObject response
    /// (the PLC sends the first full snapshot immediately) are held briefly and replayed on <see cref="Register"/>.
    /// </summary>
    public sealed class NotificationDispatcher : IDisposable
    {
        private const int QueueCapacity = 20000;
        private const int EarlyBufferPerId = 20;
        private static readonly TimeSpan EarlyBufferTtl = TimeSpan.FromSeconds(10);

        private readonly ILogger _log;
        private readonly Channel<byte[]> _queue;
        private readonly Thread _thread;
        private readonly object _lock = new object();
        private readonly Dictionary<uint, Action<Notification>> _handlers = new Dictionary<uint, Action<Notification>>();
        private readonly Dictionary<uint, List<(DateTime At, Notification N)>> _early = new Dictionary<uint, List<(DateTime, Notification)>>();
        private long _dropped;
        private volatile bool _disposed;

        public long DroppedNotifications => Interlocked.Read(ref _dropped);

        public NotificationDispatcher(S7CommPlusConnection conn, ILogger log = null)
        {
            _log = log ?? Log.Logger;
            _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            });
            conn.NotificationReceived += OnNotificationPdu;
            _thread = new Thread(Run) { IsBackground = true, Name = "S7CommPlus notifications" };
            _thread.Start();
        }

        /// <summary>Routes the notifications of a subscription to <paramref name="handler"/> (called on the dispatcher thread).</summary>
        public void Register(uint subscriptionObjectId, Action<Notification> handler)
        {
            List<(DateTime At, Notification N)> early = null;
            lock (_lock)
            {
                _handlers[subscriptionObjectId] = handler;
                if (_early.Remove(subscriptionObjectId, out var list))
                {
                    early = list;
                }
            }
            if (early != null)
            {
                foreach (var e in early)
                {
                    if (DateTime.UtcNow - e.At <= EarlyBufferTtl)
                    {
                        Invoke(handler, e.N);
                    }
                }
            }
        }

        public void Unregister(uint subscriptionObjectId)
        {
            lock (_lock)
            {
                _handlers.Remove(subscriptionObjectId);
                _early.Remove(subscriptionObjectId);
            }
        }

        private void OnNotificationPdu(byte[] pdu)
        {
            if (_disposed) return;
            if (!_queue.Writer.TryWrite(pdu))
            {
                long n = Interlocked.Increment(ref _dropped);
                if (n == 1 || n % 1000 == 0)
                {
                    _log.Warning("S7CommPlus: notification queue full, {Count} notifications dropped", n);
                }
            }
        }

        private void Run()
        {
            var reader = _queue.Reader;
            try
            {
                while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (reader.TryRead(out var pdu))
                    {
                        Dispatch(pdu);
                    }
                }
            }
            catch (Exception ex) when (!_disposed)
            {
                _log.Error(ex, "S7CommPlus: notification dispatcher stopped");
            }
            catch
            {
                // disposed
            }
        }

        private void Dispatch(byte[] pdu)
        {
            Notification n;
            try
            {
                n = Notification.DeserializeFromPdu(new MemoryStream(pdu, false));
            }
            catch (Exception ex)
            {
                _log.Warning("S7CommPlus: undecodable notification ({Length} bytes): {Error}", pdu.Length, ex.Message);
                return;
            }
            if (n == null) return;
            if (n.Truncated)
            {
                _log.Debug("S7CommPlus: notification for 0x{Id:X8} has unknown item code 0x{Code:X2}, rest ignored", n.SubscriptionObjectId, n.UnknownReturnValue);
            }

            Action<Notification> handler;
            lock (_lock)
            {
                if (!_handlers.TryGetValue(n.SubscriptionObjectId, out handler))
                {
                    if (!_early.TryGetValue(n.SubscriptionObjectId, out var list))
                    {
                        PruneEarly();
                        list = new List<(DateTime, Notification)>();
                        _early[n.SubscriptionObjectId] = list;
                    }
                    if (list.Count < EarlyBufferPerId)
                    {
                        list.Add((DateTime.UtcNow, n));
                    }
                    return;
                }
            }
            Invoke(handler, n);
        }

        // Caller holds _lock.
        private void PruneEarly()
        {
            if (_early.Count == 0) return;
            var now = DateTime.UtcNow;
            foreach (var id in _early.Where(kv => kv.Value.Count == 0 || now - kv.Value[^1].At > EarlyBufferTtl).Select(kv => kv.Key).ToList())
            {
                _early.Remove(id);
            }
        }

        private void Invoke(Action<Notification> handler, Notification n)
        {
            try
            {
                handler(n);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "S7CommPlus: notification handler failed");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Writer.TryComplete();
            _thread.Join(2000);
        }
    }
}
