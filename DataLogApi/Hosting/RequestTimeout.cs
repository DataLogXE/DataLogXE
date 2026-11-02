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

using DataLogLib.Services.DataAccess;

namespace DataLogApi.Hosting
{
    /// <summary>
    /// Runs the work of a request with a deadline. A read runs in the UI pool of the database, and its deadline cancels
    /// its statements in the database, also those of a store that takes no token: a read that only stopped waiting
    /// went on in the database (six reads for 22 minutes on the Raspberry Pi, 2026-10-31). A write runs to its end in
    /// the default pool, so it is never left half done.
    /// </summary>
    public static class RequestTimeout
    {
        public static readonly TimeSpan Read = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan Write = TimeSpan.FromSeconds(120);

        public static Task<T> RunAsync<T>(
            Func<T> work,
            TimeSpan timeout,
            CancellationToken requestAborted)
        {
            return RunAsync(_ => work(), timeout, requestAborted);
        }

        public static async Task<T> RunAsync<T>(
            Func<CancellationToken, T> work,
            TimeSpan timeout,
            CancellationToken requestAborted)
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            // A separate source for the deadline tells a real timeout apart from a cancellation that
            // the work itself triggered, for example a newer archive page superseding an older one.
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var workCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token, timeoutCts.Token);
            var isRead = timeout <= Read;
            var token = isRead ? workCts.Token : CancellationToken.None;
            try
            {
                using (isRead ? DatabaseLimits.Use(PostgresWork.Ui, token) : null)
                {
                    // The answer goes out at the deadline even when the work does not stop at once
                    return await Task.Run(() => work(token), token).WaitAsync(timeout, requestAborted);
                }
            }
            catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException();
                }

                throw;
            }
            finally
            {
                // The answer may go out before the work ends (at the deadline, or when the client left): its reads end now
                workCts.Cancel();
            }
        }

        public static async Task RunAsync(
            Action work,
            TimeSpan timeout,
            CancellationToken requestAborted)
        {
            await RunAsync<object>(() =>
            {
                work();
                return null;
            }, timeout, requestAborted);
        }
    }
}
