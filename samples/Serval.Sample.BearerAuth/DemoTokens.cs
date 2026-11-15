#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using System.Security.Claims;
using Serval.Authentication;

namespace Serval.Sample.BearerAuth;

public static class DemoTokens
{
    public static AuthenticationResult Validate(string token)
    {
        var name = token switch
        {
            "reader-demo" => "bob",
            "editor-demo" => "alice",
            _             => null,
        };
        if (name is null)
        {
            return AuthenticationResult.Fail("Unknown demo token.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, name),
            new(ClaimTypes.Name, name),
            new("permission", "reports.read"),
        };
        if (token == "editor-demo")
        {
            claims.Add(new Claim(ClaimTypes.Role, "editor"));
            claims.Add(new Claim("permission", "reports.write"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return AuthenticationResult.Success(new AuthenticationTicket(principal, "Bearer", new AuthenticationProperties()));
    }
}
