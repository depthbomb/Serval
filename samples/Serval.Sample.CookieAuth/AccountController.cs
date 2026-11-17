#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Authentication;
using Serval.Authentication.Authorization;
using Serval.Authentication.Cookies;
using Serval.Authentication.Passwords;
using Serval.Http;
using Serval.Http.Constants;
using Serval.Routing;

namespace Serval.Sample.CookieAuth;

public sealed record LoginInput(string? Login, string? Password);

[Controller]
public sealed class AccountController : ControllerBase
{
    private readonly PasswordAuthenticator _passwords;

    public AccountController(PasswordAuthenticator passwords)
    {
        _passwords = passwords;
    }

    [Get("/")]
    public ValueTask<IActionResult> IndexAsync(RequestContext context) => TextAsync("""
                                                                                    Password login, cookie sessions, and CSRF protection (loopback demo)
                                                                                    POST /account/login with Content-Type: application/json and X-Serval-Demo: login
                                                                                    Body: {"login":"demo","password":"serval-sample-password"}
                                                                                    Keep the returned cookie, then GET /account/me and GET /account/csrf.
                                                                                    POST /account/logout with the cookie and X-CSRF-Token from /account/csrf.
                                                                                    A missing or wrong CSRF token returns 403. A revoked session returns 401.
                                                                                    These credentials are public examples. Accounts and sessions reset on restart.
                                                                                    """);

    [Post("/account/login")]
    public async ValueTask<IActionResult> LoginAsync(RequestContext context)
    {
        // Requiring a custom header prevents cross-origin HTML forms from logging a
        // browser into the demo account. This sample does not enable cross-origin access.
        AbortUnless(context.Request.Headers["X-Serval-Demo"] == "login", StatusCodes.BadRequest, "Send X-Serval-Demo: login.");

        var input = await context.Request.ReadJsonAsync<LoginInput>(context.RequestAborted);
        AbortIf(string.IsNullOrWhiteSpace(input.Login) || input.Login.Length > 128 || string.IsNullOrEmpty(input.Password) || input.Password.Length > 1024, StatusCodes.BadRequest, "Login and password are required and must fit their length limits.");

        var result = await _passwords.AuthenticateAsync(input.Login, input.Password, cancellationToken: context.RequestAborted);
        AbortUnless(result.Succeeded, StatusCodes.Unauthorized, "Invalid credentials.");

        await context.SignInAsync(result.Ticket!.Principal);

        context.Response.Headers[HeaderNames.CacheControl] = "no-store";

        return Json(new
        {
            name = result.Ticket.Principal.Identity?.Name
        });
    }

    [Get("/account/me")]
    [Authorize]
    public ValueTask<IActionResult> MeAsync(RequestContext context)
    {
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return JsonAsync(new
        {
            name = context.User.Identity?.Name
        });
    }

    [Get("/account/csrf")]
    [Authorize]
    public async ValueTask<IActionResult> CsrfAsync(RequestContext context)
        => Json(new
        {
            token = await context.GetCsrfTokenAsync()
        });

    [Post("/account/logout")]
    [Authorize(Priority = -100)]
    [RequireCsrf]
    public async ValueTask<IActionResult> LogoutAsync(RequestContext context)
    {
        await context.SignOutAsync();
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        return StatusCode(StatusCodes.NoContent);
    }
}
