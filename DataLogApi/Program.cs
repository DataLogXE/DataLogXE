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

using DataLogApi.Endpoints;
using DataLogApi.Hosting;
using DataLogApi.Services;
using DataLogLib.Common.Serilog;
using DataLogLib.Services;
using DataLogLib.Services.EventLog;
using DataLogService;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Serilog;
using System.Reflection;

if (DataLogLib.OpcUaNodeset.XmlImportChild.TryRun(args, out var xmlImportExitCode))
{
    Environment.Exit(xmlImportExitCode);
}

if (DataLogLib.Services.Auth.AppUserReset.TryRun(args,
        new ConfigurationBuilder().AddEnvironmentVariables().Build(), out var resetExitCode))
{
    Environment.Exit(resetExitCode);
}

// An unhandled exception ends in abort() of the runtime. dotnet is PID 1 in a container without init, and
// the kernel ignores the SIGABRT a process sends to itself as PID 1: the process then spins at 100 % CPU
// without ever ending, and the restart policy of Docker never starts the service again. Leave through a
// normal exit there. Elsewhere the runtime ends the process by itself, after the crash was logged.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception, the process ends");
        Log.CloseAndFlush();
    }
    catch
    {
        // The process ends anyway
    }

    if (Environment.ProcessId == 1)
    {
        Environment.Exit(1);
    }
};

var builder = WebApplication.CreateBuilder(args);

var assemblyPath = Assembly.GetEntryAssembly()?.Location;
// LOG_PATH: a folder the process may write to (in Docker the app does not run as root and /app is read-only to it)
var logFolder = Environment.GetEnvironmentVariable("LOG_PATH");
if (string.IsNullOrWhiteSpace(logFolder))
{
    logFolder = assemblyPath == null ? "." : Path.GetDirectoryName(assemblyPath) ?? ".";
}
var logFile = Path.Combine(logFolder,
    assemblyPath == null ? "DataLogApi" : Path.GetFileNameWithoutExtension(assemblyPath));

var serilogSink = new SerilogSink();
// Secrets are masked before any sink sees an event. Ten files of 10 MB: the log of weeks on a Pi, and a bound for the
// support bundle.
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.With(new SecretRedactionEnricher())
    .WriteTo.File($"{logFile}.log", fileSizeLimitBytes: LogFiles.FileSizeLimitBytes, rollOnFileSizeLimit: true,
        retainedFileCountLimit: LogFiles.RetainedFileCount)
    .WriteTo.Sink(serilogSink)
    .CreateLogger();

builder.Host.UseSerilog();
builder.Services.AddSingleton(serilogSink);
builder.Services.AddSingleton(new LogFiles(logFolder, Path.GetFileName(logFile)));

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = OpcUaXmlImportAppService.MaxFileBytes;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = OpcUaXmlImportAppService.MaxFileBytes;
    options.MemoryBufferThreshold = 64 * 1024;
    options.ValueLengthLimit = 1024 * 1024;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

// Behind the TLS proxy: take the client address and the scheme from its X-Forwarded-* headers. The API port is
// only reachable inside the compose network, so the proxy is the only sender.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Sign-in of the UI: a cookie of DataLogApi (the proxy only does TLS). The cookie keys live in the data folder (a
// volume in Docker), so sessions survive restarts and deploys.
var dataFolder = Environment.GetEnvironmentVariable("DATA_PATH");
if (string.IsNullOrWhiteSpace(dataFolder))
{
    dataFolder = "/app/data";
}
builder.Services.AddDataProtection()
    .SetApplicationName("DataLogXE")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataFolder, "dataprotection")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => ApiAccess.ConfigureCookie(options, builder.Environment.IsDevelopment()));
builder.Services.AddSingleton<AuthAppService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<ArchiveStatsSnapshot>();
builder.Services.AddSingleton<QueryJobService>();
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var snapshot = sp.GetRequiredService<ArchiveStatsSnapshot>();
    var service = new ServiceCtrl(configuration);
    snapshot.Attach(service.ArchiveAccess);
    return service;
});
builder.Services.AddSingleton<DataSourceAppService>();
builder.Services.AddSingleton<DataTagAppService>();
builder.Services.AddSingleton<ChartAppService>();
builder.Services.AddSingleton<OpcUaBrowseAppService>();
builder.Services.AddSingleton<OpcUaXmlImportAppService>();
builder.Services.AddSingleton<TelegrafConfigAppService>();
builder.Services.AddSingleton<ProxyConfigAppService>();
builder.Services.AddSingleton<ArchiveSettingsAppService>();
builder.Services.AddSingleton<EventLogCtrl>();
builder.Services.AddSingleton<IEventLogWriter>(sp => sp.GetRequiredService<EventLogCtrl>());
builder.Services.AddSingleton<EventLogAppService>();
builder.Services.AddSingleton(sp =>
{
    var snapshot = sp.GetRequiredService<ArchiveStatsSnapshot>();
    var service = sp.GetRequiredService<ServiceCtrl>();
    var events = sp.GetRequiredService<EventLogCtrl>();
    return new DashboardTrends(
        () => snapshot.Get().ValuesPerSec,
        () => service.GetSystemLoadState().Percent,
        () => service.DataAccess.DataTagsCountActive(),
        // The database size is written once a minute, an hour back finds it also after a short outage
        atUtc => service.GetSystemLoadState().DatabaseDiskSizeTagId is int tagId
            ? service.ArchiveAccess.GetRawValueAt(tagId, atUtc.ToLocalTime(), TimeSpan.FromHours(1), TimeSpan.FromHours(1))?.Raw
            : null,
        (fromUtc, toUtc) => events.QueryEventCount(new EventLogQuery
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            AlarmState = AlarmStateValues.Incoming
        }));
});

var app = builder.Build();

ServiceCtrl service = null;
EventLogCtrl eventLog = null;
var exitCode = 0;
try
{
    // The service waits for the database and fails when it does not come up (see DatabaseStartupWait)
    service = app.Services.GetRequiredService<ServiceCtrl>();
    eventLog = app.Services.GetRequiredService<EventLogCtrl>();
    eventLog.Init(app.Configuration, serilogSink);
    service.EventLog = eventLog;
    app.Services.GetRequiredService<DashboardTrends>().Start();

    app.UseForwardedHeaders();
    app.UseCors();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseApiAccess();
    app.MapApiEndpoints();
    app.MapSystemEndpoints();
    app.MapFallbackToFile("index.html");

    Log.Information("Starting DataLogApi");
    app.Run();
}
catch (Exception ex)
{
    // A service that cannot start or run must end the process with an exit code, so that the restart
    // policy starts it again. An exception that leaves Main ends in abort(), which can hang (see above).
    Log.Fatal(ex, "DataLogApi terminated unexpectedly");
    exitCode = 1;
}
finally
{
    try
    {
        service?.Shutdown();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Database shutdown failed");
    }

    try
    {
        eventLog?.Flush();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Event log flush failed");
    }

    Log.CloseAndFlush();
}

if (exitCode != 0)
{
    // Not a return from Main: a thread that is not a background thread would keep the process alive
    Environment.Exit(exitCode);
}
