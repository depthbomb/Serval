#region License
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Caprine Logic
#endregion

namespace Serval.Middleware;

internal sealed class ApplicationPipeline
{
    private bool             _frozen;
    private RequestDelegate? _terminal;

    private readonly List<Func<RequestContext, RequestDelegate, ValueTask>> _middleware = [];

    internal void Add(Func<RequestContext, RequestDelegate, ValueTask> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        if (_frozen)
        {
            throw new InvalidOperationException("Middleware registration is closed after the host starts.");
        }

        _middleware.Add(middleware);
    }

    internal RequestDelegate Build(RequestDelegate terminal)
    {
        _frozen  = true;
        terminal = _terminal ?? terminal;
        for (var index = _middleware.Count - 1; index >= 0; index--)
        {
            var middleware = _middleware[index];
            var next       = terminal;
            terminal = context => middleware(context, next);
        }

        return terminal;
    }

    internal void SetTerminal(RequestDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (_frozen)
        {
            throw new InvalidOperationException("Dispatch configuration is closed after the host starts.");
        }

        _terminal = terminal;
    }
}
