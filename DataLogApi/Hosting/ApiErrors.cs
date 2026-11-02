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

namespace DataLogApi.Hosting
{
    /// <summary>Maps an exception of a request or a query job to the status and message the UI shows.</summary>
    public static class ApiErrors
    {
        public static (int Status, string Message) Describe(Exception ex)
        {
            switch (ex)
            {
                case TimeoutException:
                    return (StatusCodes.Status408RequestTimeout, "The request timed out.");
                case OperationCanceledException:
                    return (499, "The request was superseded by a newer request.");
                // The statement timeout of the UI pool (see DatabaseLimits)
                case Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.QueryCanceled } canceled
                    when canceled.MessageText.Contains("statement timeout", StringComparison.OrdinalIgnoreCase):
                    Serilog.Log.Warning("A query of the UI reached the time limit of {Seconds} s",
                        DataLogLib.Services.DataAccess.DatabaseLimits.Profile.UiStatementTimeout.TotalSeconds);
                    return (StatusCodes.Status503ServiceUnavailable,
                        $"The database stopped the query after {DataLogLib.Services.DataAccess.DatabaseLimits.Profile.UiStatementTimeout.TotalSeconds:0} s. "
                        + "The system may be busy; please try again later or choose a shorter range.");
                case UnauthorizedAccessException unauthorized:
                    return (StatusCodes.Status401Unauthorized, unauthorized.Message);
                case ArgumentException argument:
                    return (StatusCodes.Status400BadRequest, argument.Message);
                case InvalidDataException invalidData:
                    return (StatusCodes.Status400BadRequest, invalidData.Message);
                case KeyNotFoundException notFound:
                    return (StatusCodes.Status404NotFound, notFound.Message);
                case InvalidOperationException invalid:
                    var status = invalid.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                        || invalid.Message.Contains("already running", StringComparison.OrdinalIgnoreCase)
                        || invalid.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase)
                        || invalid.Message.Contains("only available for OPC UA", StringComparison.OrdinalIgnoreCase)
                        ? StatusCodes.Status409Conflict
                        : StatusCodes.Status400BadRequest;
                    return (status, invalid.Message);
                default:
                    Serilog.Log.Error(ex, "API request failed");
                    return (StatusCodes.Status500InternalServerError, "The request failed: " + ex.Message);
            }
        }
    }
}
