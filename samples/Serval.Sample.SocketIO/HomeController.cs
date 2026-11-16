#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

using Serval.Http;
using Serval.Routing;

namespace Serval.Sample.SocketIO;

[Controller]
public sealed class HomeController : ControllerBase
{
    [Get("/")]
    public ValueTask<IActionResult> IndexAsync()
        => FileAsync(Path.Combine(AppContext.BaseDirectory, "Public", "socketio", "index.html"), "text/html; charset=utf-8");
}
