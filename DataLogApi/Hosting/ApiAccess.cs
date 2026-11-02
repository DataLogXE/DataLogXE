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
using DataLogApi.Services;
using DataLogLib.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace DataLogApi.Hosting
{
    /// <summary>
    /// Endpoint metadata: a request other than GET that a viewer may send (reads such as the query jobs).
    /// </summary>
    public sealed class ViewerAllowed
    {
        public static readonly ViewerAllowed Instance = new();
    }

    /// <summary>
    /// Endpoint metadata: answers without a signed-in user (status, login, setup, Telegraf config, proxy Caddyfile).
    /// </summary>
    public sealed class AnonymousAllowed
    {
        public static readonly AnonymousAllowed Instance = new();
    }

    /// <summary>
    /// Default deny for /api: without a signed-in user 401, a viewer may only read (GET, HEAD and endpoints marked
    /// <see cref="ViewerAllowed"/>), otherwise 403. A new endpoint is therefore admin-only for writes. The static UI
    /// files stay open, the login page is part of them.
    /// </summary>
    public static class ApiAccess
    {
        public const string CookieName = "DataLogXE.Auth";
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

        public static IApplicationBuilder UseApiAccess(this IApplicationBuilder app)
        {
            return app.Use(async (context, next) =>
            {
                if (!context.Request.Path.StartsWithSegments("/api"))
                {
                    await next();
                    return;
                }

                var endpoint = context.GetEndpoint();
                if (endpoint?.Metadata.GetMetadata<AnonymousAllowed>() != null)
                {
                    await next();
                    return;
                }

                var user = context.User;
                if (user?.Identity?.IsAuthenticated != true)
                {
                    await Deny(context, StatusCodes.Status401Unauthorized, "Please sign in.");
                    return;
                }

                var isRead = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
                if (!user.IsInRole(AppUserModel.RoleAdmin) && !isRead
                    && endpoint?.Metadata.GetMetadata<ViewerAllowed>() == null)
                {
                    await Deny(context, StatusCodes.Status403Forbidden, "This needs the administrator role.");
                    return;
                }

                await next();
            });
        }

        /// <summary>
        /// The cookie of a signed-in user: 30 days, extended while used; ends when the user's stamp changes.
        /// </summary>
        public static void ConfigureCookie(CookieAuthenticationOptions options, bool development)
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Behind the TLS proxy the scheme is https (forwarded headers); Vite in development talks plain http.
            options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = SessionLifetime;
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                // A pure API: answer with a status instead of redirecting to a login page
                OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                },
                OnValidatePrincipal = async context =>
                {
                    var auth = context.HttpContext.RequestServices.GetRequiredService<AuthAppService>();
                    var id = AuthAppService.UserId(context.Principal);
                    var current = id == null ? null : auth.GetCurrent(id.Value);
                    var stamp = context.Principal?.FindFirst(AuthAppService.StampClaim)?.Value;
                    if (current == null || current.SecurityStamp != stamp)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                }
            };
        }

        private static Task Deny(HttpContext context, int status, string message)
        {
            context.Response.StatusCode = status;
            return context.Response.WriteAsJsonAsync(new ErrorDto { Error = message });
        }
    }
}
