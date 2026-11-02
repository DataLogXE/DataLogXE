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

using System.Collections.Concurrent;

namespace DataLogLib.OpcUaNodeset
{
    public static class XmlImportChild
    {
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length < 2 || !string.Equals(args[0], XmlImportLimits.ArgName, StringComparison.Ordinal))
            {
                return false;
            }

            exitCode = Run(args[1]);
            return true;
        }

        public static int Run(string jobPath)
        {
            XmlImportProgressPublisher publisher = null;
            try
            {
                LinuxProcessLimits.TryApplyAddressSpaceLimit(XmlImportLimits.AddressSpaceLimitBytes);
                var job = XmlImportJob.Load(jobPath);
                var counters = new XmlImportCounters
                {
                    FileBytes = new FileInfo(job.SourcePath).Length
                };
                publisher = new XmlImportProgressPublisher(job.StatusPath, counters);
                publisher.Start();

                using var cancellation = new CancellationTokenSource();
                var cancelWatch = new Thread(() => WatchCancel(job.CancelPath, cancellation))
                {
                    IsBackground = true,
                    Name = "XmlImportCancel"
                };
                cancelWatch.Start();

                var types = new XmlImportTypeGraph();
                var pool = new ConcurrentBag<List<XmlImportRow>>();
                using var batches = new BlockingCollection<List<XmlImportRow>>(XmlImportLimits.QueueBatches);
                Exception readerError = null;
                Exception writerError = null;
                var writer = new Thread(() =>
                {
                    try
                    {
                        XmlImportSidecar.Write(
                            job.JobId,
                            batches,
                            pool,
                            counters,
                            types,
                            publisher.SetPhase,
                            cancellation.Token);
                    }
                    catch (Exception ex)
                    {
                        if (ex is not OperationCanceledException)
                        {
                            Console.Error.WriteLine(ex);
                        }

                        writerError = ex;
                        try
                        {
                            cancellation.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                            // The import is already leaving.
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "XmlImportWrite"
                };
                writer.Start();

                try
                {
                    var renames = new List<KeyValuePair<string, string>>();
                    OpcUaNodesetImportParser.Parse(
                        job.SourcePath,
                        types,
                        batches,
                        pool,
                        counters,
                        cancellation.Token,
                        renames);
                    XmlImportJob.SaveRenames(job.StatusPath, renames);
                }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine(ex);
                    }

                    readerError = ex;
                }
                finally
                {
                    batches.CompleteAdding();
                }

                writer.Join();
                if (File.Exists(job.CancelPath))
                {
                    publisher.Fail(XmlImportLimits.CancelledMessage);
                    return 2;
                }

                // A failed writer cancels the reader, so its error comes first; the reader then only reports the cancel.
                if (writerError != null && writerError is not OperationCanceledException)
                {
                    throw writerError;
                }

                if (readerError != null)
                {
                    throw readerError;
                }

                if (writerError != null)
                {
                    throw writerError;
                }

                publisher.SetPhase("ready");
                return 0;
            }
            catch (OperationCanceledException)
            {
                publisher?.Fail(XmlImportLimits.CancelledMessage);
                return 2;
            }
            catch (OutOfMemoryException ex)
            {
                Console.Error.WriteLine(ex);
                publisher?.Fail(XmlImportLimits.RanOutOfMemoryMessage);
                return 3;
            }
            catch (InvalidDataException ex)
            {
                publisher?.Fail(XmlImportMemory.Describe(ex));
                return 1;
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine(ex);
                }

                publisher?.Fail(XmlImportMemory.Describe(ex));
                return 1;
            }
            finally
            {
                publisher?.Dispose();
            }
        }

        private static void WatchCancel(string cancelPath, CancellationTokenSource cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(cancelPath))
                    {
                        cancellation.Cancel();
                        return;
                    }
                }
                catch (Exception)
                {
                    // The next pass reports the missing cancel file.
                }

                Thread.Sleep(100);
            }
        }
    }
}
