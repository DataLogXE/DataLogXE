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
using DataLogService;
using DataLogService.DataSource.OpcUa;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DataLogApi.Endpoints
{
    public static class ApiEndpoints
    {
        public static WebApplication MapApiEndpoints(this WebApplication app)
        {
            var api = app.MapGroup("/api");

            api.MapGet("/dashboard", async (
                DataSourceAppService sources,
                ArchiveStatsSnapshot stats,
                DashboardTrends trends,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    () => sources.GetDashboard(stats.Get(), trends.GetSnapshot()),
                    RequestTimeout.Read,
                    cancellationToken);
            });

            api.MapGet("/datasources", async (
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(sources.GetList, RequestTimeout.Read, cancellationToken);
            });

            api.MapGet("/datasources/{id:int}", async (
                int id,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => sources.GetDetail(id), RequestTimeout.Write, cancellationToken);
            });

            api.MapPost("/datasources", async (
                DataSourceWriteDto dto,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => sources.Create(dto), RequestTimeout.Write, cancellationToken, StatusCodes.Status201Created);
            });

            api.MapPost("/datasources/opcua/connection-test", async (
                OpcUaConnectionTestDto dto,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    token => sources.TestOpcUaConnection(dto, token), RequestTimeout.Read, cancellationToken);
            });

            api.MapGet("/opcua/client-certificate", async (
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => new OpcUaClientCertificateDto
                {
                    Certificate = OpcUaCertificateInfo.From(sources.GetOpcUaClientCertificate())
                }, RequestTimeout.Read, cancellationToken);
            });

            // The certificate only (DER), never the private key: servers add it to their trusted certificates.
            api.MapGet("/opcua/client-certificate/download", async (
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                var certificate = await Task.Run(sources.GetOpcUaClientCertificate, cancellationToken);
                return Results.File(certificate.RawData, "application/pkix-cert", "DataLogXE-OpcUaClient.der");
            });

            api.MapPost("/datasources/s7commplus/connection-test", async (
                OpcUaConnectionTestDto dto,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    () => sources.TestS7CommPlusConnection(dto), RequestTimeout.Read, cancellationToken);
            });

            api.MapPut("/datasources/{id:int}", async (
                int id,
                DataSourceWriteDto dto,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => sources.Update(id, dto), RequestTimeout.Write, cancellationToken);
            });

            api.MapDelete("/datasources/{id:int}", async (
                int id,
                DataSourceAppService sources,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() =>
                {
                    sources.Delete(id);
                    return (object)null;
                }, RequestTimeout.Write, cancellationToken, StatusCodes.Status204NoContent);
            });

            api.MapGet("/datasources/log-counts", async (
                EventLogAppService events,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(events.GetSourceLogCounts, RequestTimeout.Read, cancellationToken);
            });

            api.MapGet("/datasources/{id:int}/log", async (
                int id,
                string before,
                string after,
                string level,
                string q,
                int? limit,
                EventLogAppService events,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    ct => events.GetSourceLog(id, before, after, level, q, limit, ct),
                    RequestTimeout.Read,
                    cancellationToken);
            });

            api.MapGet("/datasources/{id:int}/tags", async (
                int id,
                string q,
                bool? activeOnly,
                int? offset,
                int? limit,
                string sort1,
                string dir1,
                string sort2,
                string dir2,
                DataTagAppService tags,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    () => tags.GetPage(
                        id,
                        q,
                        activeOnly ?? true,
                        offset ?? 0,
                        limit ?? DataTagAppService.DefaultListLimit,
                        sort1,
                        dir1,
                        sort2,
                        dir2),
                    RequestTimeout.Read,
                    cancellationToken);
            });

            api.MapPost("/datasources/{id:int}/tags/actual-values", async (
                int id,
                DataTagIdsRequestDto dto,
                DataTagAppService tags,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => tags.GetActualValues(id, dto), RequestTimeout.Read, cancellationToken);
            }).WithMetadata(ViewerAllowed.Instance);

            api.MapPost("/datasources/{id:int}/tags/active", async (
                int id,
                DataTagActiveWriteDto dto,
                DataTagAppService tags,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => tags.SetActive(id, dto), RequestTimeout.Write, cancellationToken);
            });

            api.MapPost("/datasources/{id:int}/tags/cycle", async (
                int id,
                DataTagCycleWriteDto dto,
                DataTagAppService tags,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => tags.SetCycle(id, dto), RequestTimeout.Write, cancellationToken);
            });

            api.MapPost("/datasources/{id:int}/tags/details", async (
                int id,
                DataTagIdsRequestDto dto,
                DataTagAppService tags,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => tags.GetDetails(id, dto), RequestTimeout.Read, cancellationToken);
            }).WithMetadata(ViewerAllowed.Instance);

            api.MapPost("/datasources/{id:int}/tags/update", async (
                int id,
                DataTagUpdateDto dto,
                DataTagAppService tags,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => tags.Update(id, dto), RequestTimeout.Write, cancellationToken);
            });

            // The raw data export as CSV, streamed while the archive is read: the browser saves it as a download, so its
            // size has no limit. With check the request is only checked (204 or the error), before the download starts.
            api.MapGet("/export/raw.csv", async (
                [FromQuery] string[] tag,
                double from,
                double to,
                bool? decimalComma,
                [FromQuery] string[] offset,
                string file,
                bool? check,
                DataTagAppService tags,
                CancellationToken cancellationToken) =>
            {
                List<RawCsvTag> list;
                RawCsvExport export;
                try
                {
                    list = await RequestTimeout.RunAsync(() => tags.GetRawCsvTags(tag, from, to), RequestTimeout.Read, cancellationToken);
                    export = new RawCsvExport(decimalComma == true, RawCsvExport.ParseOffsets(offset));
                }
                catch (Exception ex)
                {
                    return ToErrorResult(ex);
                }
                if (check == true)
                {
                    return Results.NoContent();
                }

                // An error after the first byte aborts the response, so the browser marks the download as failed
                return Results.Stream(
                    body => export.WriteAsync(body, list, item => tags.StreamRawValues(item.TagId, from, to, cancellationToken), cancellationToken),
                    "text/csv; charset=utf-8",
                    string.IsNullOrWhiteSpace(file) ? "raw data.csv" : file);
            });

            api.MapPost("/datasources/{id:int}/tags/browse", async (
                int id,
                OpcUaBrowseAppService browse,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => browse.Start(id), RequestTimeout.Write, cancellationToken, StatusCodes.Status202Accepted);
            });

            api.MapGet("/datasources/{id:int}/tags/browse", async (
                int id,
                OpcUaBrowseAppService browse,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => browse.Get(id), RequestTimeout.Read, cancellationToken);
            });

            api.MapDelete("/datasources/{id:int}/tags/browse", async (
                int id,
                OpcUaBrowseAppService browse,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() =>
                {
                    browse.Cancel(id);
                    return (object)null;
                }, RequestTimeout.Write, cancellationToken, StatusCodes.Status204NoContent);
            });

            api.MapPost("/datasources/{id:int}/tags/import-xml", async (
                int id,
                HttpRequest request,
                OpcUaXmlImportAppService import,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var dto = await import.StartAsync(id, request, cancellationToken);
                    return Results.Json(dto, statusCode: StatusCodes.Status202Accepted);
                }
                catch (Exception ex)
                {
                    return ToErrorResult(ex);
                }
            }).DisableAntiforgery();

            api.MapGet("/datasources/{id:int}/tags/import-xml", async (
                int id,
                OpcUaXmlImportAppService import,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => import.Get(id), RequestTimeout.Read, cancellationToken);
            });

            api.MapDelete("/datasources/{id:int}/tags/import-xml", async (
                int id,
                OpcUaXmlImportAppService import,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() =>
                {
                    import.Cancel(id);
                    return (object)null;
                }, RequestTimeout.Write, cancellationToken, StatusCodes.Status204NoContent);
            });

            api.MapGet("/settings/archive", async (
                ArchiveSettingsAppService archiveSettings,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(archiveSettings.Get, RequestTimeout.Read, cancellationToken);
            });

            api.MapPut("/settings/archive/retention", async (
                ArchiveRetentionWriteDto dto,
                ArchiveSettingsAppService archiveSettings,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    () => archiveSettings.SetRetention(dto.EverySeconds),
                    RequestTimeout.Write,
                    cancellationToken);
            });

            // Telegraf fetches its config inside the compose network without a login; it holds no secrets.
            api.MapGet("/telegraf.conf", (TelegrafConfigAppService telegrafConfig) =>
            {
                return Results.Text(telegrafConfig.Build(), "text/plain; charset=utf-8");
            }).WithMetadata(AnonymousAllowed.Instance);

            // The proxy (official Caddy image) fetches its Caddyfile the same way at its start; it holds no secrets.
            api.MapGet("/caddyfile", (ProxyConfigAppService proxyConfig) =>
            {
                return Results.Text(proxyConfig.Caddyfile, "text/plain; charset=utf-8");
            }).WithMetadata(AnonymousAllowed.Instance);

            api.MapGet("/alarm-classes", async (
                EventLogAppService events,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    () => events.GetAlarmClasses(),
                    RequestTimeout.Read,
                    cancellationToken);
            });

            api.MapGet("/events/active", async (
                string sourceIds,
                EventLogAppService events,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(
                    () => events.GetActiveAlarms(sourceIds),
                    RequestTimeout.Read,
                    cancellationToken);
            });

            api.MapGet("/charts", async (
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(charts.GetTree, RequestTimeout.Read, cancellationToken);
            });

            api.MapGet("/charts/{id:int}", async (
                int id,
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => charts.GetChart(id), RequestTimeout.Read, cancellationToken);
            });

            api.MapPost("/charts", async (
                ChartCreateDto dto,
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => charts.Create(dto), RequestTimeout.Write, cancellationToken, StatusCodes.Status201Created);
            });

            api.MapPut("/charts/{id:int}", async (
                int id,
                ChartUpdateDto dto,
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => charts.Update(id, dto), RequestTimeout.Write, cancellationToken);
            });

            api.MapDelete("/charts/{id:int}", async (
                int id,
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() =>
                {
                    charts.Delete(id);
                    return (object)null;
                }, RequestTimeout.Write, cancellationToken, StatusCodes.Status204NoContent);
            });

            api.MapPost("/charts/{id:int}/move", async (
                int id,
                ChartMoveDto dto,
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() =>
                {
                    charts.Move(id, dto);
                    return (object)null;
                }, RequestTimeout.Write, cancellationToken, StatusCodes.Status204NoContent);
            });

            api.MapPost("/charts/{id:int}/sort", async (
                int id,
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() =>
                {
                    charts.SortChildren(id);
                    return (object)null;
                }, RequestTimeout.Write, cancellationToken, StatusCodes.Status204NoContent);
            });

            api.MapPost("/charts/{id:int}/plots", async (
                int id,
                ChartPlotsAppendDto dto,
                ChartAppService charts,
                CancellationToken cancellationToken) =>
            {
                return await ExecuteAsync(() => charts.AppendPlots(id, dto), RequestTimeout.Write, cancellationToken);
            });

            api.MapQueryJobEndpoints();
            api.MapAuthEndpoints();
            return app;
        }

        private static async Task<IResult> ExecuteAsync<T>(
            Func<CancellationToken, T> work,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            int successStatus = StatusCodes.Status200OK)
        {
            try
            {
                var result = await RequestTimeout.RunAsync(work, timeout, cancellationToken);
                if (successStatus == StatusCodes.Status204NoContent)
                {
                    return Results.NoContent();
                }

                return Results.Json(result, statusCode: successStatus);
            }
            catch (Exception ex)
            {
                return ToErrorResult(ex);
            }
        }

        private static async Task<IResult> ExecuteAsync<T>(
            Func<T> work,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            int successStatus = StatusCodes.Status200OK)
        {
            try
            {
                var result = await RequestTimeout.RunAsync(work, timeout, cancellationToken);
                if (successStatus == StatusCodes.Status204NoContent)
                {
                    return Results.NoContent();
                }

                return Results.Json(result, statusCode: successStatus);
            }
            catch (Exception ex)
            {
                return ToErrorResult(ex);
            }
        }

        private static IResult ToErrorResult(Exception ex)
        {
            var (status, message) = ApiErrors.Describe(ex);
            return Results.Json(new ErrorDto { Error = message }, statusCode: status);
        }
    }
}
