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

using DataLogApi.Dtos;
using DataLogApi.Hosting;
using DataLogApi.Services;
using DataLogLib.Models;
using Serilog;

namespace DataLogApi.Endpoints
{
    /// <summary>
    /// The system log and its downloads, for the administrator only (also for reading: the log names hosts, addresses
    /// and the inner workings of the system).
    /// </summary>
    public static class SystemEndpoints
    {
        public static WebApplication MapSystemEndpoints(this WebApplication app)
        {
            var system = app.MapGroup("/api/system").AddEndpointFilter(async (context, next) =>
            {
                if (!context.HttpContext.User.IsInRole(AppUserModel.RoleAdmin))
                {
                    return Results.Json(new ErrorDto { Error = "This needs the administrator role." },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                return await next(context);
            });

            system.MapGet("/log", async (
                string before,
                string after,
                string level,
                string component,
                int? source,
                string correlation,
                string q,
                int? limit,
                EventLogAppService events,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var page = await RequestTimeout.RunAsync(
                        ct => events.GetSystemLog(before, after, level, component, source, correlation, q, limit, ct),
                        RequestTimeout.Read,
                        cancellationToken);
                    return Results.Json(page);
                }
                catch (Exception ex)
                {
                    return ToErrorResult(ex);
                }
            });

            // The system log as CSV or NDJSON, streamed while it is read. With check the request is only checked (204
            // or the error), before the browser starts the download.
            system.MapGet("/log/export", (
                string from,
                string to,
                string level,
                string component,
                int? source,
                string correlation,
                string q,
                string format,
                bool? decimalComma,
                bool? check,
                HttpContext http,
                EventLogAppService events,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                DataLogLib.Services.EventLog.AppLogQuery filter;
                DateTime fromUtc, toUtc;
                string normalizedFormat;
                try
                {
                    (fromUtc, toUtc) = ParseRange(from, to);
                    filter = EventLogAppService.BuildAppLogFilter(level, component, source, correlation, q);
                    normalizedFormat = SystemLogExport.NormalizeFormat(format);
                }
                catch (Exception ex)
                {
                    return ToErrorResult(ex);
                }

                if (check == true)
                {
                    return Results.NoContent();
                }

                if (!SystemLogExport.TryEnter())
                {
                    return Busy();
                }

                Log.Information("System log: {UserName} exported the log from {From:o} to {To:o} as {Format}",
                    http.User.Identity?.Name, fromUtc, toUtc, normalizedFormat);
                // An error after the first byte aborts the response, so the browser marks the download as failed
                return Results.Stream(async body =>
                {
                    try
                    {
                        await SystemLogExport.WriteAsync(
                            body,
                            events.ExportSystemLog(filter, fromUtc, toUtc, cancellationToken),
                            normalizedFormat,
                            decimalComma == true,
                            SourceNames(sources),
                            cancellationToken);
                    }
                    finally
                    {
                        SystemLogExport.Exit();
                    }
                },
                SystemLogExport.ContentType(normalizedFormat),
                SystemLogExport.FileName(fromUtc, toUtc, normalizedFormat));
            });

            // The support bundle: a ZIP file of the log, the log files and the state of the system. With check the
            // request is only checked (204 or the error), before the browser starts the download.
            system.MapGet("/diagnostics.zip", (
                int? hours,
                bool? check,
                HttpContext http,
                IConfiguration configuration,
                LogFiles logFiles,
                EventLogAppService events,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                int range;
                try
                {
                    range = DiagnosticsBundle.NormalizeHours(hours);
                }
                catch (Exception ex)
                {
                    return ToErrorResult(ex);
                }

                if (DataLogLib.Services.LoadGuard.Shared.Level == DataLogLib.Services.LoadLevel.Critical)
                {
                    return Results.Json(
                        new ErrorDto { Error = "The system is under critical load. Please try again in a few minutes." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if (check == true)
                {
                    return Results.NoContent();
                }

                if (!SystemLogExport.TryEnter())
                {
                    return Busy();
                }

                var userName = http.User.Identity?.Name;
                Log.Information("System log: {UserName} downloaded the support bundle of the last {Hours} hours", userName, range);
                // The ZIP writer writes its directory synchronously at the end
                var bodyControl = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
                if (bodyControl != null)
                {
                    bodyControl.AllowSynchronousIO = true;
                }

                var nowUtc = DateTime.UtcNow;
                return Results.Stream(async body =>
                {
                    try
                    {
                        await new DiagnosticsBundle(configuration, logFiles, events, sources)
                            .WriteAsync(body, range, userName, cancellationToken);
                    }
                    finally
                    {
                        SystemLogExport.Exit();
                    }
                },
                "application/zip",
                DiagnosticsBundle.FileName(nowUtc));
            });

            return app;
        }

        /// <summary>From and to as ISO 8601; by default the last 24 hours, at most <see cref="SystemLogExport.MaxRange"/>.</summary>
        internal static (DateTime FromUtc, DateTime ToUtc) ParseRange(string from, string to)
        {
            var nowUtc = DateTime.UtcNow;
            var toUtc = string.IsNullOrWhiteSpace(to) ? nowUtc : EventLogAppService.ParseCursorUtc(to);
            var fromUtc = string.IsNullOrWhiteSpace(from) ? toUtc.AddHours(-24) : EventLogAppService.ParseCursorUtc(from);
            if (toUtc > nowUtc.AddMinutes(1))
            {
                toUtc = nowUtc.AddMinutes(1);
            }

            if (fromUtc >= toUtc)
            {
                throw new ArgumentException("The start of the range must be before its end.");
            }

            if (toUtc - fromUtc > SystemLogExport.MaxRange + TimeSpan.FromMinutes(1))
            {
                throw new ArgumentException("The range spans at most 14 days.");
            }

            return (fromUtc, toUtc);
        }

        internal static IReadOnlyDictionary<int, string> SourceNames(DataSourceAppService sources)
        {
            try
            {
                return sources.GetList().ToDictionary(s => s.Id, s => s.Name ?? "");
            }
            catch (Exception ex)
            {
                // The export goes on with the ids alone
                Log.Warning(ex, "System log: the names of the data sources cannot be read");
                return new Dictionary<int, string>();
            }
        }

        internal static IResult Busy() =>
            Results.Json(new ErrorDto { Error = "Another export is running. Please try again when it has finished." },
                statusCode: StatusCodes.Status429TooManyRequests);

        private static IResult ToErrorResult(Exception ex)
        {
            var (status, message) = ApiErrors.Describe(ex);
            return Results.Json(new ErrorDto { Error = message }, statusCode: status);
        }
    }
}
