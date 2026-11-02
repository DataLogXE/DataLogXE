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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace DataLogApi.Endpoints
{
    /// <summary>
    /// Sign-in of the web UI and the user list of the administrator.
    /// </summary>
    public static class AuthEndpoints
    {
        private const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

        public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
        {
            var auth = api.MapGroup("/auth");

            auth.MapGet("/status", (HttpContext context, AuthAppService service) =>
            {
                return Results.Json(new AuthStatusDto
                {
                    SetupRequired = service.SetupRequired(),
                    User = AuthAppService.ToAuthUser(context.User)
                });
            }).WithMetadata(AnonymousAllowed.Instance);

            auth.MapPost("/setup", (LoginDto dto, HttpContext context, AuthAppService service) =>
                SignInAsync(context, () => service.Setup(dto, Address(context))))
                .WithMetadata(AnonymousAllowed.Instance);

            auth.MapPost("/login", (LoginDto dto, HttpContext context, AuthAppService service) =>
                SignInAsync(context, () => service.Login(dto, Address(context))))
                .WithMetadata(AnonymousAllowed.Instance);

            auth.MapPost("/logout", async (HttpContext context) =>
            {
                await context.SignOutAsync(Scheme);
                return Results.NoContent();
            }).WithMetadata(AnonymousAllowed.Instance);

            auth.MapPost("/password", (PasswordChangeDto dto, HttpContext context, AuthAppService service) =>
                SignInAsync(context, () => service.ChangeOwnPassword(AuthAppService.UserId(context.User) ?? 0, dto)))
                .WithMetadata(ViewerAllowed.Instance);

            var users = api.MapGroup("/users");

            users.MapGet("", (HttpContext context, AuthAppService service) =>
                AdminOnly(context, () => Results.Json(service.GetUsers())));

            users.MapPost("", (AppUserWriteDto dto, HttpContext context, AuthAppService service) =>
                AdminOnly(context, () => Results.Json(service.CreateUser(dto, context.User.Identity?.Name),
                    statusCode: StatusCodes.Status201Created)));

            users.MapPut("/{id:int}", (int id, AppUserWriteDto dto, HttpContext context, AuthAppService service) =>
                AdminOnly(context, () => Results.Json(service.UpdateUser(id, dto, context.User.Identity?.Name))));

            users.MapDelete("/{id:int}", (int id, HttpContext context, AuthAppService service) =>
                AdminOnly(context, () =>
                {
                    service.DeleteUser(id, context.User.Identity?.Name);
                    return Results.NoContent();
                }));

            return api;
        }

        /// <summary>
        /// Runs the check and signs the user in with a 30 day cookie. The password check runs off the request thread
        /// (PBKDF2 takes a moment on a Raspberry Pi).
        /// </summary>
        private static async Task<IResult> SignInAsync(HttpContext context, Func<DataLogLib.Models.AppUserModel> check)
        {
            try
            {
                var user = await Task.Run(check);
                var principal = AuthAppService.ToPrincipal(user, Scheme);
                await context.SignInAsync(Scheme, principal, new AuthenticationProperties { IsPersistent = true });
                return Results.Json(new AuthStatusDto { SetupRequired = false, User = AuthAppService.ToAuthUser(principal) });
            }
            catch (Exception ex)
            {
                var (status, message) = ApiErrors.Describe(ex);
                return Results.Json(new ErrorDto { Error = message }, statusCode: status);
            }
        }

        // The user list also needs the role for reading
        private static IResult AdminOnly(HttpContext context, Func<IResult> work)
        {
            if (!context.User.IsInRole(DataLogLib.Models.AppUserModel.RoleAdmin))
            {
                return Results.Json(new ErrorDto { Error = "This needs the administrator role." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                return work();
            }
            catch (Exception ex)
            {
                var (status, message) = ApiErrors.Describe(ex);
                return Results.Json(new ErrorDto { Error = message }, statusCode: status);
            }
        }

        private static string Address(HttpContext context)
        {
            return context.Connection.RemoteIpAddress?.ToString() ?? "";
        }
    }
}
