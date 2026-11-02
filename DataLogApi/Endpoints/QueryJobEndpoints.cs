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
using DataLogLib.Services;

namespace DataLogApi.Endpoints
{
    /// <summary>
    /// Database reads of the UI as background jobs. A POST takes the same query parameters as the
    /// matching GET endpoint and answers at once with the job state; the client then polls
    /// /api/jobs/{id} until the job is done and cancels it with DELETE when it no longer needs it. The
    /// client names the job in the X-Query-Job-Id header, so a DELETE also reaches a job whose start
    /// the client abandoned before the answer came.
    /// </summary>
    public static class QueryJobEndpoints
    {
        // The id the client chose for the job, so it can cancel a job whose start it no longer waits for.
        public const string JobIdHeader = "X-Query-Job-Id";

        public static RouteGroupBuilder MapQueryJobEndpoints(this RouteGroupBuilder api)
        {
            // All jobs are reads: a viewer may start, poll and cancel them
            var jobs = api.MapGroup("/jobs").WithMetadata(ViewerAllowed.Instance);

            // All unfinished jobs of the server; mine lists the job ids of the caller
            jobs.MapGet("/running", (string mine, QueryJobService service) =>
                Results.Json(service.Running((mine ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))));

            jobs.MapPost("/running/{key}/stop", (string key, HttpContext context, QueryJobService service) =>
            {
                if (!context.User.IsInRole(DataLogLib.Models.AppUserModel.RoleAdmin))
                {
                    return Results.Json(new ErrorDto { Error = "This needs the administrator role." },
                        statusCode: StatusCodes.Status403Forbidden);
                }
                return service.Stop(key, context.User.Identity?.Name)
                    ? Results.NoContent()
                    : Results.Json(new ErrorDto { Error = "The query is no longer running." }, statusCode: StatusCodes.Status404NotFound);
            });

            api.MapGet("/system/load", () =>
            {
                var guard = LoadGuard.Shared;
                return Results.Json(new SystemLoadDto
                {
                    Level = LoadGuard.Describe(guard.Level),
                    Reason = guard.Reason,
                    Seconds = Math.Round(guard.LevelFor.TotalSeconds)
                });
            }).WithMetadata(ViewerAllowed.Instance);

            jobs.MapGet("/{jobId}", async (
                string jobId,
                int? waitMs,
                QueryJobService service,
                CancellationToken cancellationToken) =>
            {
                QueryJob job;
                try
                {
                    job = service.Get(jobId);
                }
                catch (KeyNotFoundException ex)
                {
                    return Results.Json(new ErrorDto { Error = ex.Message }, statusCode: StatusCodes.Status404NotFound);
                }

                // Long poll: answer as soon as the job is done, at the latest after the wait.
                var wait = TimeSpan.FromMilliseconds(Math.Clamp(waitMs ?? 0, 0, (int)QueryJobService.MaxPollWait.TotalMilliseconds));
                if (wait > TimeSpan.Zero && !job.IsFinished)
                {
                    try
                    {
                        await job.Completion.WaitAsync(wait, cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                        return Results.Empty;
                    }
                }

                job.Touch();
                return Results.Json(job.ToDto(jobId.ToLowerInvariant()));
            });

            jobs.MapDelete("/{jobId}", (string jobId, QueryJobService service) =>
            {
                service.Cancel(jobId);
                return Results.NoContent();
            });

            jobs.MapPost("/events/page", (
                double? from,
                double? to,
                string level,
                string kind,
                int? sourceId,
                string sourceIds,
                string component,
                string state,
                string q,
                int? limit,
                string cursor,
                int? offset,
                string sort1,
                string dir1,
                string classes,
                EventLogAppService events,
                QueryJobService service,
                HttpContext http) => Start(service, http, "events/page", (progress, token) =>
                    events.GetPage(from, to, level, kind, sourceId, sourceIds, component, state, q, limit, cursor, offset, sort1, dir1, token, progress, classes)));

            jobs.MapPost("/events/count", (
                double? from,
                double? to,
                string level,
                string kind,
                int? sourceId,
                string sourceIds,
                string component,
                string state,
                string q,
                string classes,
                EventLogAppService events,
                QueryJobService service,
                HttpContext http) => Start(service, http, "events/count", (progress, token) =>
                    events.GetCount(from, to, level, kind, sourceId, sourceIds, component, state, q, token, progress, classes)));

            jobs.MapPost("/events/summary", (
                double? from,
                double? to,
                string level,
                string kind,
                int? sourceId,
                string sourceIds,
                string component,
                string state,
                EventLogAppService events,
                QueryJobService service,
                HttpContext http) => Start(service, http, "events/summary", (progress, token) =>
                    events.GetSummary(from, to, level, kind, sourceId, sourceIds, component, state, token, progress)));

            jobs.MapPost("/alarm-states", (
                double? from,
                double? to,
                string sourceIds,
                string classes,
                EventLogAppService events,
                QueryJobService service,
                HttpContext http) => Start(service, http, "alarm-states", (progress, token) =>
                    events.GetAlarmStates(from, to, sourceIds, token, progress, classes)));

            jobs.MapPost("/events/alarm-history", (
                string correlationId,
                double? from,
                double? to,
                int? sourceId,
                EventLogAppService events,
                QueryJobService service,
                HttpContext http) => Start(service, http, "events/alarm-history", (progress, token) =>
                    events.GetAlarmHistory(correlationId, from, to, sourceId, token, progress)));

            jobs.MapPost("/events/alarm-counts", (
                string correlationId,
                int? sourceId,
                double? time,
                string state,
                string edges,
                EventLogAppService events,
                QueryJobService service,
                HttpContext http) => Start(service, http, "events/alarm-counts", (progress, token) =>
                    events.GetAlarmCounts(correlationId, sourceId, time, state, edges, token, progress), QueryCost.Small));

            jobs.MapPost("/datasources/{id:int}/tags/{tagId:int}/plot-points", (
                int id,
                int tagId,
                double from,
                double to,
                int? width,
                DataTagAppService tags,
                QueryJobService service,
                HttpContext http) => Start(service, http, "plot-points", (progress, token) =>
                    tags.GetPlotPoints(id, tagId, from, to, width, token, progress)));

            // Bounded reads of a compression level, small so they come before the exact points
            jobs.MapPost("/datasources/{id:int}/tags/{tagId:int}/plot-preview", (
                int id,
                int tagId,
                double from,
                double to,
                int? width,
                DataTagAppService tags,
                QueryJobService service,
                HttpContext http) => Start(service, http, "plot-preview", (progress, token) =>
                    tags.GetPlotPreview(id, tagId, from, to, width, token, progress), QueryCost.Small));

            // The raw value at a measuring cursor: a bounded read (see TimescaleArchive.GetRawValueAt), small so the
            // cursors of a stack of plots do not wait behind its chart loads; the client cancels a superseded one
            jobs.MapPost("/datasources/{id:int}/tags/{tagId:int}/value-at", (
                int id,
                int tagId,
                double time,
                DataTagAppService tags,
                QueryJobService service,
                HttpContext http) => Start(service, http, "value-at", (_, token) =>
                    tags.GetRawValueAt(id, tagId, time, token), QueryCost.Small));

            jobs.MapPost("/datasources/{id:int}/tags/{tagId:int}/statistics", (
                int id,
                int tagId,
                double from,
                double to,
                DataTagAppService tags,
                QueryJobService service,
                HttpContext http) => Start(service, http, "statistics", (progress, token) =>
                    tags.GetStatistics(id, tagId, from, to, token, progress),
                    estimate: token => service.CostOfRows(tags.EstimateRawRows(id, tagId, from, to, token))));

            // The raw data export: the number of values for the estimate (fast, from the counters) and the values
            jobs.MapPost("/datasources/{id:int}/tags/{tagId:int}/raw-count", (
                int id,
                int tagId,
                double from,
                double to,
                DataTagAppService tags,
                QueryJobService service,
                HttpContext http) => Start(service, http, "raw-count", (progress, token) =>
                    tags.GetRawValueCount(id, tagId, from, to, token, progress), QueryCost.Small));

            jobs.MapPost("/datasources/{id:int}/tags/{tagId:int}/raw-values", (
                int id,
                int tagId,
                double from,
                double to,
                int? limit,
                DataTagAppService tags,
                QueryJobService service,
                HttpContext http) => Start(service, http, "raw-values", (progress, token) =>
                    tags.GetRawValues(id, tagId, from, to, limit, token, progress),
                    estimate: token => service.CostOfRows(tags.EstimateRawRows(id, tagId, from, to, token))));

            return api;
        }

        // The request (path and query) tells which jobs give the same result; a second client of one joins the job
        private static IResult Start(
            QueryJobService service,
            HttpContext http,
            string kind,
            Func<IQueryProgress, CancellationToken, object> work,
            QueryCost cost = QueryCost.Medium,
            Func<CancellationToken, QueryCost> estimate = null)
        {
            var (id, job) = service.Start(
                kind, work, http.Request.Headers[JobIdHeader].ToString(), cost, estimate,
                http.Request.Path.Value + http.Request.QueryString.Value);
            return Results.Json(job.ToDto(id), statusCode: StatusCodes.Status202Accepted);
        }
    }
}
